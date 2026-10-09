import {
  BookApi,
  type BookCommand,
  type CharacterLineDto,
  type CharacterSummaryDto,
  CharactersApi,
  type CommandResponse,
  type Guid,
  toApiError,
} from '@app/api';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { type BookFacet, type Receipt, hasFacet } from '@app/live/live-messages';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { Debounced } from '@app/shared/debounced';
import { ToastService } from '@app/ui/toast';

/**
 * Facets that change a roster row: the cast itself, the narrator link, voices (readiness) and
 * attribution (line counts, and the selected character's lines).
 */
const CAST_FACETS: readonly BookFacet[] = ['Characters', 'Narrator', 'Voices', 'Attribution'];

/**
 * The cast page's state (ticket 15, design §9 "Cast"): the roster summary, the selected character
 * and its lines. Writes post a command and reload from the response (design §8 "optimistic
 * nothing"); receipts from elsewhere — another tab, an agent, an attribution run — reload too,
 * debounced per burst. Provided by the cast page, so it lives as long as the page does. The
 * selected character's voices and voice rules, the roster as alias owners for discovery and the
 * voice-batch patching arrive with the voices screens (native-web 31).
 */
export class CastStore {
  private readonly characters = use(CharactersApi);
  private readonly book = use(BookApi);
  private readonly live = use(LiveService);
  private readonly toast = use(ToastService);

  private subscriptions: Unsubscribe[] = [];
  private readonly refetch = new Debounced(() => this.refresh());

  private readonly _folder = signal<string | null>(null);
  private readonly _rows = signal<CharacterSummaryDto[]>([]);
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);
  private readonly _selectedId = signal<Guid | null>(null);
  private readonly _lines = signal<CharacterLineDto[]>([]);
  private readonly _linesLoading = signal(false);
  private readonly _busy = signal(false);

  readonly folder = this._folder.asReadonly();
  readonly rows = this._rows.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();
  readonly selectedId = this._selectedId.asReadonly();
  readonly lines = this._lines.asReadonly();
  readonly linesLoading = this._linesLoading.asReadonly();
  /** A write is in flight; mutation affordances are off. */
  readonly busy = this._busy.asReadonly();

  /** The selected row, or null when nothing is selected or the id no longer exists. */
  readonly selected = computed(() => {
    const id = this._selectedId();
    return id === null ? null : (this._rows().find((r) => r.id === id) ?? null);
  });

  /** Loads the roster and listens for receipts that change it. */
  async open(folder: string): Promise<void> {
    this.close();
    this._folder.set(folder);
    this._rows.set([]);
    this._error.set(null);
    this.subscriptions = [this.live.receipts(folder, (r) => this.onReceipt(r))];

    this._loading.set(true);
    try {
      const rows = await this.characters.summary(folder);
      if (this._folder() === folder) this._rows.set(rows);
    } catch (error) {
      if (this._folder() === folder) this._error.set(toApiError(error).message);
    } finally {
      if (this._folder() === folder) this._loading.set(false);
    }
  }

  close(): void {
    for (const unsubscribe of this.subscriptions) unsubscribe();
    this.subscriptions = [];
    this.refetch.cancel();
    this._folder.set(null);
    this._selectedId.set(null);
    this._lines.set([]);
  }

  /** Selects a character and loads its lines; null clears the detail. */
  async select(id: Guid | null): Promise<void> {
    this._selectedId.set(id);
    this._lines.set([]);
    if (id === null) return;
    await this.loadLines(id);
  }

  /** Reloads the roster and, when one is selected, its lines. */
  async refresh(): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    const selected = this._selectedId();
    const [rows] = await Promise.all([
      this.characters.summary(folder),
      selected ? this.loadLines(selected) : Promise.resolve(),
    ]);
    if (this._folder() === folder) this._rows.set(rows);
  }

  /**
   * Posts one command and reloads. Resolves the host's response (for `newEntityId`), or undefined
   * when the host refused it — the refusal is toasted verbatim (design §8).
   */
  async run(command: BookCommand): Promise<CommandResponse | undefined> {
    const folder = this._folder();
    if (!folder || this._busy()) return undefined;
    this._busy.set(true);
    try {
      const response = await this.book.execute(folder, command);
      await this.refresh();
      return response;
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return undefined;
    } finally {
      this._busy.set(false);
    }
  }

  private async loadLines(id: Guid): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    this._linesLoading.set(true);
    try {
      const lines = await this.characters.lines(folder, id);
      if (this._selectedId() === id) this._lines.set(lines);
    } finally {
      if (this._selectedId() === id) this._linesLoading.set(false);
    }
  }

  private onReceipt(r: Receipt): void {
    if (CAST_FACETS.some((f) => hasFacet(r.effects.facets, f))) this.refetch.schedule();
  }
}
