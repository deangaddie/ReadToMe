import { html, nothing } from 'lit-html';
import {
  type PronunciationDraftRequest,
  type PronunciationMatchInBook,
  PronunciationsApi,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { blockable, icon, spinner } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import {
  gpuBusyReason,
  playBlockedReason,
  playView,
  spokenPlayBlockedReason,
} from './try-out-logic';
import { TryOutPlayer } from './try-out-player';
import css from './pronunciation-sentence.css' with { type: 'text' };

adoptStyles(css);

/**
 * The Book view panel's try-out (pronunciation ticket 16): in place of Try it's 3 cards, the one
 * sentence the editor was opened from, as TTS will say it with the draft, played in the item's own
 * voice with the draft spoken form or as written. Off with its reason while a step is viewed or a
 * GPU job is queued or running; nothing is stored.
 */
export class PronunciationSentence extends R2mElement {
  private readonly api = use(PronunciationsApi);
  private readonly live = use(LiveService);
  private readonly toast = use(ToastService);

  readonly #folder = signal<string | null>(null);
  get folder() {
    return this.#folder();
  }
  set folder(value: string | null) {
    this.#folder.set(value);
  }

  /** The draft's match in that sentence. */
  readonly #match = signal<PronunciationMatchInBook | null>(null);
  get match() {
    return this.#match();
  }
  set match(value: PronunciationMatchInBook | null) {
    this.#match.set(value);
  }

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

  readonly player = new TryOutPlayer({
    render: (request, abort) => this.api.tryOut(this.#folder()!, request, abort),
    failed: (error) => this.toast.problem(error.toProblem()),
  });

  private readonly blocked = computed(() =>
    playBlockedReason(this.#readOnly(), gpuBusyReason(this.live.queue(), this.live.voiceBatch())),
  );

  protected override connected(): void {
    this.classList.add('pron-sentence');
    this.onDisconnect(() => this.player.stop());
  }

  private play(m: PronunciationMatchInBook, asWritten: boolean, label: string) {
    const key = asWritten ? 'written' : 'spoken';
    const view = playView(key, this.blocked(), this.player.playing());
    const reason =
      view.reason ??
      (asWritten ? null : spokenPlayBlockedReason(this.#draft()?.spokenForm ?? null));
    return blockable(
      reason,
      html`<button
        type="button"
        class="r2m-button r2m-button--text"
        data-action="play-${key}"
        aria-pressed=${view.phase === 'idle' ? 'false' : 'true'}
        ?disabled=${reason !== null}
        @click=${() =>
          this.player.toggle(key, [
            {
              itemId: m.itemId,
              sentenceStart: m.sentenceStart,
              draft: this.#draft(),
              asWritten,
              voice: 'item',
            },
          ])}
      >
        ${
          view.phase === 'generating'
            ? html`${spinner(16)} Generating…`
            : html`${icon(view.phase === 'playing' ? 'stop' : 'play_arrow')} ${label}`
        }
      </button>`,
    );
  }

  protected template() {
    const m = this.#match();
    if (!m) return nothing;
    const end = m.spokenMatchStart + m.spokenMatchLength;
    return html`<p class="pron-sentence__text">
        ${m.spokenSentence.slice(0, m.spokenMatchStart)}<mark
          title=${m.sentence.slice(m.matchStart, m.matchStart + m.matchLength)}
          >${m.spokenSentence.slice(m.spokenMatchStart, end)}</mark
        >${m.spokenSentence.slice(end)}
      </p>
      <div class="pron-sentence__actions">
        ${this.play(m, false, 'Spoken form')} ${this.play(m, true, 'As written')}
      </div>`;
  }
}
define('r2m-pronunciation-sentence', PronunciationSentence);
