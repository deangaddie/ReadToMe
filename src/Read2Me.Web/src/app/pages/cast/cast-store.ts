import { Injectable, computed, inject, signal } from '@angular/core';
import {
  BookApi,
  BookCommand,
  CharacterLineDto,
  CharacterSummaryDto,
  CharactersApi,
  CommandResponse,
  Guid,
  toApiError,
} from '@app/api';
import { BookFacet, Receipt, hasFacet } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { Subscription } from 'rxjs';
import { Debounced } from '@app/shared/debounced';
import { AliasOwner } from './alias-collisions';

/**
 * Facets that change a roster row: the cast itself, the narrator link, voices (readiness) and
 * attribution (line counts, and the selected character's lines).
 */
const CAST_FACETS: readonly BookFacet[] = ['Characters', 'Narrator', 'Voices', 'Attribution'];

/**
 * The cast page's state (ticket 15, design §9 "Cast"): the roster summary, the selected character
 * and its lines. Writes post a command and reload from the response (design §8 "optimistic
 * nothing"); receipts from elsewhere — another tab, the Blazor UI, an attribution run — reload
 * too, debounced per burst. Provided by the cast page, so it lives as long as the page does.
 */
@Injectable()
export class CastStore {
  private readonly characters = inject(CharactersApi);
  private readonly book = inject(BookApi);
  private readonly live = inject(LiveService);
  private readonly toast = inject(ToastService);

  private subscription: Subscription | null = null;
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

  /** The roster as collision-check owners (what the discovery dialog folds rows onto). */
  readonly roster = computed<AliasOwner[]>(() =>
    this._rows().map((r) => ({ id: r.id, name: r.name, aliases: r.aliases.map((a) => a.name) })),
  );

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
    this.subscription = this.live.receipts$(folder).subscribe((r) => this.onReceipt(r));

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
    this.subscription?.unsubscribe();
    this.subscription = null;
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
