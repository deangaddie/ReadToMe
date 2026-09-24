import { Injectable, computed, inject, signal } from '@angular/core';
import {
  BookApi,
  BookCommand,
  CharacterLineDto,
  CharacterSummaryDto,
  CharacterVoicesDto,
  ChapterVoicePreviewDto,
  CharactersApi,
  CommandResponse,
  Guid,
  VoiceRuleDto,
  VoicesApi,
  toApiError,
} from '@app/api';
import { BookFacet, Receipt, VoiceBatchMessage, hasFacet } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { Subscription } from 'rxjs';
import { Debounced } from '@app/shared/debounced';
import { AliasOwner } from './alias-collisions';
import { applyVoiceUpdated } from './voices/voice-logic';

/**
 * Facets that change a roster row: the cast itself, the narrator link, voices (readiness) and
 * attribution (line counts, and the selected character's lines).
 */
const CAST_FACETS: readonly BookFacet[] = ['Characters', 'Narrator', 'Voices', 'Attribution'];

/**
 * Facets that change the selected character's voice rules or their preview (ticket 17): the rules
 * themselves, voices (a rename shows in the rows) and structure (a deleted node dangles a rule,
 * a new chapter adds a preview row).
 */
const VOICE_RULE_FACETS: readonly BookFacet[] = ['VoiceRules', 'Voices', 'Structure'];

const NO_VOICES: CharacterVoicesDto = { defaultVoiceId: null, voices: [] };

/**
 * The cast page's state (ticket 15, design §9 "Cast"): the roster summary, the selected character,
 * its lines and its voices (16). Writes post a command and reload from the response (design §8
 * "optimistic nothing"); receipts from elsewhere — another tab, the Blazor UI, an attribution run —
 * reload too, debounced per burst. A running voice batch patches the selected character's voices in
 * place from `voiceBatch.voiceUpdated` and reloads on `completed`. Provided by the cast page, so it
 * lives as long as the page does.
 */
@Injectable()
export class CastStore {
  private readonly characters = inject(CharactersApi);
  private readonly voicesApi = inject(VoicesApi);
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
  private readonly _voices = signal<CharacterVoicesDto>(NO_VOICES);
  private readonly _voicesLoading = signal(false);
  private readonly _voiceRules = signal<VoiceRuleDto[]>([]);
  private readonly _voiceRulePreview = signal<ChapterVoicePreviewDto[]>([]);
  private readonly _voiceRulesLoading = signal(false);
  private readonly _audioVersions = signal<Record<Guid, number>>({});
  private readonly _busy = signal(false);

  readonly folder = this._folder.asReadonly();
  readonly rows = this._rows.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();
  readonly selectedId = this._selectedId.asReadonly();
  readonly lines = this._lines.asReadonly();
  readonly linesLoading = this._linesLoading.asReadonly();
  /** The selected character's voices and default voice id. */
  readonly voices = this._voices.asReadonly();
  readonly voicesLoading = this._voicesLoading.asReadonly();
  /** The selected character's voice rules in evaluation order, and the voice each chapter resolves to. */
  readonly voiceRules = this._voiceRules.asReadonly();
  readonly voiceRulePreview = this._voiceRulePreview.asReadonly();
  readonly voiceRulesLoading = this._voiceRulesLoading.asReadonly();
  /**
   * Per-voice cache-buster: bumped whenever this tab learns a voice's audio changed (upload,
   * generation, a batch's `voiceUpdated`), so a regenerated file under the same name is refetched.
   */
  readonly audioVersions = this._audioVersions.asReadonly();
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

  /** Any character has a voice — the prompt batch then asks for its scope (research §4). */
  readonly anyVoices = computed(() => this._rows().some((r) => r.voiceCount > 0));

  /** Loads the roster and listens for receipts that change it. */
  async open(folder: string): Promise<void> {
    this.close();
    this._folder.set(folder);
    this._rows.set([]);
    this._error.set(null);
    this.subscription = new Subscription();
    this.subscription.add(this.live.receipts$(folder).subscribe((r) => this.onReceipt(r)));
    this.subscription.add(this.live.on('voiceBatch').subscribe((m) => this.onVoiceBatch(m)));

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
    this._voices.set(NO_VOICES);
    this._voiceRules.set([]);
    this._voiceRulePreview.set([]);
  }

  /** Selects a character and loads its lines and voices; null clears the detail. */
  async select(id: Guid | null): Promise<void> {
    this._selectedId.set(id);
    this._lines.set([]);
    this._voices.set(NO_VOICES);
    this._voiceRules.set([]);
    this._voiceRulePreview.set([]);
    if (id === null) return;
    await Promise.all([this.loadLines(id), this.loadVoices(id), this.loadVoiceRules(id)]);
  }

  /** Reloads the roster and, when one is selected, its lines and voices. */
  async refresh(): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    const selected = this._selectedId();
    const [rows] = await Promise.all([
      this.characters.summary(folder),
      selected ? this.loadLines(selected) : Promise.resolve(),
      selected ? this.loadVoices(selected) : Promise.resolve(),
      selected ? this.loadVoiceRules(selected) : Promise.resolve(),
    ]);
    if (this._folder() === folder) this._rows.set(rows);
  }

  /** Reloads only the selected character's voices (after a per-voice endpoint answered). */
  async refreshVoices(): Promise<void> {
    const selected = this._selectedId();
    if (selected) await this.loadVoices(selected);
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

  /** Marks a voice's audio as changed so its player refetches under the same file name. */
  bumpAudio(voiceId: Guid): void {
    this._audioVersions.update((v) => ({ ...v, [voiceId]: (v[voiceId] ?? 0) + 1 }));
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

  private async loadVoices(id: Guid): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    this._voicesLoading.set(true);
    try {
      const voices = await this.voicesApi.list(folder, id);
      if (this._selectedId() === id) this._voices.set(voices);
    } finally {
      if (this._selectedId() === id) this._voicesLoading.set(false);
    }
  }

  private async loadVoiceRules(id: Guid): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    this._voiceRulesLoading.set(true);
    try {
      const [rules, preview] = await Promise.all([
        this.characters.voiceRules(folder, id),
        this.characters.voiceRulePreview(folder, id),
      ]);
      if (this._selectedId() === id) {
        this._voiceRules.set(rules);
        this._voiceRulePreview.set(preview);
      }
    } finally {
      if (this._selectedId() === id) this._voiceRulesLoading.set(false);
    }
  }

  private onReceipt(r: Receipt): void {
    const facets = this._selectedId() ? [...CAST_FACETS, ...VOICE_RULE_FACETS] : CAST_FACETS;
    if (facets.some((f) => hasFacet(r.effects.facets, f))) this.refetch.schedule();
  }

  /**
   * `voiceUpdated` lands on the selected character's card in place, one voice at a time, so the
   * page shows a batch's progress without a reload per voice; `completed` / `cancelled` reload
   * everything (a prompt batch creates voices the patch cannot invent).
   */
  private onVoiceBatch(m: VoiceBatchMessage): void {
    switch (m.kind) {
      case 'voiceUpdated': {
        if (m.characterId !== this._selectedId() || !m.voiceId) return;
        const patched = applyVoiceUpdated(this._voices(), m);
        if (patched === this._voices()) {
          // A voice this tab has not seen yet (a prompt batch just created it): fetch the list.
          this.refetch.schedule();
          return;
        }
        this._voices.set(patched);
        if (m.audioFileName) this.bumpAudio(m.voiceId);
        return;
      }
      case 'completed':
      case 'cancelled':
        this.refetch.schedule();
        return;
      default:
        return;
    }
  }
}
