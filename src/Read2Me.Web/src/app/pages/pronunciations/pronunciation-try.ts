import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import {
  CharactersApi,
  type CharacterVoicesDto,
  type Guid,
  type PronunciationDraftRequest,
  type PronunciationMatchInBook,
  PronunciationsApi,
  type TryPronunciationRequest,
  type TryVoice,
  VoicesApi,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { blockable, icon, spinner } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import {
  ITEM_VOICE,
  type SampleCard,
  type VoicePicker,
  cardsFor,
  gpuBusyReason,
  matchKey,
  otherSampleBlockedReason,
  playBlockedReason,
  reshuffle,
  spokenPlayBlockedReason,
  swap,
  toggleCard,
  voiceFromValue,
  voicePicker,
  voiceValue,
} from './try-out-logic';
import { type TryOutPlaying, TryOutPlayer } from './try-out-player';
import css from './pronunciation-try.css' with { type: 'text' };

adoptStyles(css);

/** What every play button reads, read once per render. */
interface PlayView {
  playing: TryOutPlaying | null;
  blocked: string | null;
}

/**
 * Try it (ticket 13, variant F): up to 3 sample cards picked from the draft's matches, each played
 * with the draft spoken form or as written in the voice its picker names, Play all, Other samples
 * and the All N matches list whose ticks make cards. One sample plays at a time; every play is off
 * with its reason while a step is viewed or a GPU job is queued or running. Nothing is stored.
 */
export class PronunciationTry extends R2mElement {
  private readonly api = use(PronunciationsApi);
  private readonly characters = use(CharactersApi);
  private readonly voices = use(VoicesApi);
  private readonly live = use(LiveService);
  private readonly toast = use(ToastService);

  readonly #folder = signal<string | null>(null);
  get folder() {
    return this.#folder();
  }
  set folder(value: string | null) {
    if (value !== this.#folder()) this.picker.set(null);
    this.#folder.set(value);
  }

  /** The impact's matches for the draft, in book order. */
  readonly #matches = signal<readonly PronunciationMatchInBook[]>([]);
  get matchesInBook() {
    return this.#matches();
  }
  set matchesInBook(value: readonly PronunciationMatchInBook[]) {
    this.#matches.set(value);
  }

  /** The draft the spoken-form plays put in its entry's place. */
  readonly #draft = signal<PronunciationDraftRequest | null>(null);
  get draft() {
    return this.#draft();
  }
  set draft(value: PronunciationDraftRequest | null) {
    this.#draft.set(value);
  }

  /** `StepView.readOnly`: why nothing plays while a step is viewed, or null. */
  readonly #readOnly = signal<string | null>(null);
  get readOnly() {
    return this.#readOnly();
  }
  set readOnly(value: string | null) {
    this.#readOnly.set(value);
  }

  /** The project's voices for the pickers; null until read. */
  readonly picker = signal<VoicePicker | null>(null);
  readonly cards = signal<SampleCard[]>([]);
  readonly showAll = signal(false);
  readonly player = new TryOutPlayer({
    render: (request, abort) => this.api.tryOut(this.#folder()!, request, abort),
    // A refusal (the GPU taken since the button lit, TTS busy or down, no voice) is a toast, as every refusal is.
    failed: (error) => this.toast.problem(error.toProblem()),
  });

  private readonly blocked = computed(() =>
    playBlockedReason(this.#readOnly(), gpuBusyReason(this.live.queue(), this.live.voiceBatch())),
  );

  protected override connected(): void {
    this.classList.add('pron-try');
    this.onDisconnect(() => this.player.stop());
    this.effect(() => {
      const folder = this.#folder();
      if (folder !== null) untracked(() => void this.loadVoices(folder));
    });
    // Cards follow the matches: kept while their matches stand, picked afresh when not.
    this.effect(() => {
      const matches = this.#matches();
      const picker = this.picker();
      if (picker === null) return;
      untracked(() => this.cards.set(cardsFor(matches, this.cards(), picker.defaultVoice)));
    });
  }

  private async loadVoices(folder: string): Promise<void> {
    let picker: VoicePicker;
    try {
      const cast = await this.characters.summary(folder);
      const voices = new Map<Guid, CharacterVoicesDto>();
      await Promise.all(
        cast
          .filter((c) => c.readyVoiceCount > 0)
          .map(async (c) => voices.set(c.id, await this.voices.list(folder, c.id))),
      );
      picker = voicePicker(cast, voices);
    } catch {
      // Without the cast, cards still play in each item's own voice.
      picker = { groups: [], defaultVoice: 'item' };
    }
    if (this.#folder() === folder) this.picker.set(picker);
  }

  private request(
    m: PronunciationMatchInBook,
    asWritten: boolean,
    voice: TryVoice,
  ): TryPronunciationRequest {
    return {
      itemId: m.itemId,
      sentenceStart: m.sentenceStart,
      draft: this.#draft(),
      asWritten,
      voice,
    };
  }

  private setVoice(index: number, value: string): void {
    const card = this.cards()[index];
    if (!card) return;
    if (
      this.player.playing()?.key.startsWith(`${card.key}|`) ||
      this.player.playing()?.key === 'all'
    ) {
      this.player.stop();
    }
    this.cards.set(
      this.cards().map((c, i) => (i === index ? { ...c, voice: voiceFromValue(value) } : c)),
    );
  }

  /** A play button: ▶ label, "Generating…" while its render runs, ■ while it sounds; off with a reason. */
  private playButton(
    view: PlayView,
    key: string,
    label: string,
    requests: () => TryPronunciationRequest[],
    extra: string | null = null,
  ) {
    const mine = view.playing?.key === key;
    const reason = view.blocked ?? extra;
    return blockable(
      reason,
      html`<button
        type="button"
        class="r2m-button r2m-button--text pron-try__play"
        data-play=${key}
        aria-pressed=${mine ? 'true' : 'false'}
        ?disabled=${reason !== null}
        @click=${() => this.player.toggle(key, requests())}
      >
        ${
          mine && view.playing?.phase === 'generating'
            ? html`${spinner(16)} Generating…`
            : html`${icon(mine ? 'stop' : 'play_arrow')} ${label}`
        }
      </button>`,
    );
  }

  /** The sentence as TTS will be asked to say it, the match's spoken form (its case carried over) marked. */
  private sentence(m: PronunciationMatchInBook) {
    const spoken = m.spokenSentence;
    const end = m.spokenMatchStart + m.spokenMatchLength;
    return html`<p class="pron-try__sentence">
      ${spoken.slice(0, m.spokenMatchStart)}<mark
        class="pron-try__mark"
        title=${m.sentence.slice(m.matchStart, m.matchStart + m.matchLength)}
        >${spoken.slice(m.spokenMatchStart, end)}</mark
      >${spoken.slice(end)}
    </p>`;
  }

  private voiceSelect(picker: VoicePicker, card: SampleCard, index: number) {
    if (picker.groups.length === 0) return nothing;
    const value = voiceValue(card.voice);
    return html`<select
      class="pron-try__voice"
      aria-label="Voice"
      data-role="voice"
      .value=${value}
      @change=${(e: Event) => this.setVoice(index, (e.target as HTMLSelectElement).value)}
    >
      ${picker.groups.map(
        (g) =>
          html`<optgroup label=${g.character}>
            ${g.voices.map(
              (v) =>
                html`<option value=${v.id} ?selected=${v.id === value}>
                  ${g.character}: ${v.name}
                </option>`,
            )}
          </optgroup>`,
      )}
      <option value=${ITEM_VOICE} ?selected=${value === ITEM_VOICE}>This item's own voice</option>
    </select>`;
  }

  protected template() {
    const picker = this.picker();
    const matches = this.#matches();
    if (matches.length === 0) return nothing;
    // Read once here: `repeat` item templates must not read signals themselves (web.md Gotchas).
    const view: PlayView = { playing: this.player.playing(), blocked: this.blocked() };
    const noSpoken = spokenPlayBlockedReason(this.#draft()?.spokenForm ?? null);
    const byKey = new Map(matches.map((m) => [matchKey(m), m]));
    const cards = this.cards().filter((c) => byKey.has(c.key));
    const noOther = otherSampleBlockedReason(matches.length, cards.length);
    const shown = new Set(cards.map((c) => c.key));
    const showAll = this.showAll();
    const rowVoice = picker?.defaultVoice ?? 'item';
    return html`
      <div class="pron-try__head">
        <h3 class="pron-try__title">Try it</h3>
        ${this.playButton(
          view,
          'all',
          'Play all',
          () => cards.map((c) => this.request(byKey.get(c.key)!, false, c.voice)),
          noSpoken,
        )}
      </div>
      ${picker === null ? html`<p class="pron-try__loading">${spinner(16)} Loading voices…</p>` : nothing}
      ${repeat(
        cards,
        (c) => c.key,
        (card, index) => {
          const m = byKey.get(card.key)!;
          return html`<article class="pron-try__card" data-key=${card.key}>
            <p class="pron-try__meta" data-role="meta">
              ${m.chapterTitle ?? 'Untitled chapter'} · ${m.speaker ?? 'Unattributed'} ·
              ${m.hasAudio ? 'has audio' : 'no audio'}
            </p>
            ${this.sentence(m)}
            <div class="pron-try__actions">
              ${picker ? this.voiceSelect(picker, card, index) : nothing}
              ${this.playButton(view, `${card.key}|spoken`, 'Spoken form', () => [this.request(m, false, card.voice)], noSpoken)}
              ${this.playButton(view, `${card.key}|written`, 'As written', () => [this.request(m, true, card.voice)])}
              <span class="pron-try__spacer"></span>
              ${blockable(
                noOther,
                html`<button
                  type="button"
                  class="r2m-icon-button"
                  data-action="swap"
                  aria-label="Swap for another sentence"
                  data-tooltip=${noOther === null ? 'Swap for another sentence' : nothing}
                  ?disabled=${noOther !== null}
                  @click=${() => this.cards.set(swap(matches, this.cards(), index))}
                >
                  ${icon('refresh')}
                </button>`,
              )}
            </div>
          </article>`;
        },
      )}
      <div class="pron-try__more">
        ${blockable(
          noOther,
          html`<button
            type="button"
            class="r2m-button r2m-button--text"
            data-action="other-samples"
            ?disabled=${noOther !== null}
            @click=${() => this.cards.set(reshuffle(matches, this.cards()))}
          >
            ${icon('restart_alt')} Other samples
          </button>`,
        )}
        <button
          type="button"
          class="r2m-button r2m-button--text"
          data-action="all-matches"
          aria-expanded=${showAll ? 'true' : 'false'}
          @click=${() => this.showAll.set(!showAll)}
        >
          ${icon('list')} All ${matches.length} match${matches.length === 1 ? '' : 'es'}
        </button>
      </div>
      ${
        showAll
          ? html`<ul class="pron-try__all" data-role="all-matches">
              ${repeat(
                matches,
                (m) => matchKey(m),
                (m) => {
                  const key = matchKey(m);
                  return html`<li class="pron-try__row" data-key=${key}>
                    <label class="r2m-checkbox">
                      <input
                        type="checkbox"
                        aria-label="Show as a sample"
                        .checked=${shown.has(key)}
                        @change=${() => this.cards.set(toggleCard(this.cards(), key, rowVoice))}
                      />
                    </label>
                    ${this.playButton(view, `row:${key}`, 'Play', () => [this.request(m, false, rowVoice)], noSpoken)}
                    <span class="pron-try__row-meta"
                      >${m.chapterTitle ?? 'Untitled chapter'} ·
                      ${m.speaker ?? 'Unattributed'}</span
                    >
                    ${this.sentence(m)}
                  </li>`;
                },
              )}
            </ul>`
          : nothing
      }
    `;
  }
}
define('r2m-pronunciation-try', PronunciationTry);
