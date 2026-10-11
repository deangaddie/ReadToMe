import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import type { AudioGenEvent } from '@app/live/hub-events';
import { type AudioCard, type AudioPhaseState, foldAudioCards } from './audio-cards';
import { Autoscroll } from './autoscroll';
import type { IconName } from './icons';
import { icon, spinner, statusChip } from './partials';

const PHASE_ICON: Record<AudioPhaseState, IconName> = {
  pending: 'radio_button_unchecked',
  active: 'progress_activity',
  ok: 'check_circle',
  warn: 'warning',
  error: 'error',
};

const isDone = (state: AudioPhaseState): boolean => state === 'ok' || state === 'warn';

function cardChip(card: AudioCard) {
  if (card.failed) return statusChip({ status: 'error', label: 'Failed', compact: true });
  const verify = card.phases[4]!.state;
  if (isDone(verify)) {
    return statusChip({
      status: verify === 'ok' ? 'ok' : 'warn',
      label: verify === 'ok' ? 'Verified' : 'Rescued',
      compact: true,
    });
  }
  return statusChip({ status: 'busy', label: 'Running', compact: true });
}

/**
 * Audio pipeline cards (design §7): one card per item attempt with the five phase steps,
 * autoscroll that pauses when the user scrolls up. An element for the same reason as
 * `r2m-stream-llm`: the pause state and the after-render scroll.
 */
export class StreamAudio extends R2mElement {
  #events = signal<readonly AudioGenEvent[]>([]);
  get events() {
    return this.#events();
  }
  set events(value: readonly AudioGenEvent[]) {
    this.#events.set(value);
  }

  #maxCards = signal(50);
  get maxCards() {
    return this.#maxCards();
  }
  set maxCards(value: number) {
    this.#maxCards.set(value);
  }

  readonly #cards = computed(() => foldAudioCards(this.#events(), this.#maxCards()));
  readonly #scroll = new Autoscroll(() =>
    this.querySelector<HTMLElement>('.r2m-stream-audio__scroll'),
  );
  readonly paused = this.#scroll.paused;

  protected override connected(): void {
    this.classList.add('r2m-stream-audio');
  }

  protected override updated(): void {
    this.#scroll.afterRender();
  }

  protected template() {
    const cards = this.#cards();
    return html`
      <div class="r2m-stream-audio__scroll" @scroll=${this.#scroll.onScroll}>
        ${
          cards.length === 0
            ? html`<p class="r2m-stream-audio__empty">No audio generation yet.</p>`
            : nothing
        }
        ${repeat(
          cards,
          (card) => card.key,
          (card) =>
            html`<article
              class="r2m-stream-audio__card ${card.failed ? 'r2m-stream-audio__card--failed' : ''}"
            >
              <header class="r2m-stream-audio__head">
                <span class="r2m-stream-audio__character"
                  >${card.character || 'Unknown speaker'}</span
                >
                <span class="r2m-stream-audio__attempt">attempt ${card.attempt}</span>
                ${cardChip(card)}
              </header>
              ${card.text ? html`<p class="r2m-stream-audio__text">${card.text}</p>` : nothing}
              <ol class="r2m-stream-audio__phases">
                ${card.phases.map(
                  (phase) =>
                    html`<li class="r2m-stream-audio__phase" data-state=${phase.state}>
                      ${phase.state === 'active' ? spinner(16) : icon(PHASE_ICON[phase.state])}
                      <span class="r2m-stream-audio__phase-label">${phase.label}</span>
                      ${
                        phase.lines.length
                          ? html`<ul class="r2m-stream-audio__lines">
                              ${phase.lines.map((line) => html`<li>${line}</li>`)}
                            </ul>`
                          : nothing
                      }
                    </li>`,
                )}
              </ol>
              ${card.reason ? html`<p class="r2m-stream-audio__reason">${card.reason}</p>` : nothing}
            </article>`,
        )}
      </div>
      ${
        this.paused()
          ? html`<button
              type="button"
              class="r2m-button r2m-button--stroked r2m-stream-audio__jump"
              @click=${this.#scroll.jumpToLatest}
            >
              ${icon('arrow_downward')}Jump to latest
            </button>`
          : nothing
      }
    `;
  }
}
define('r2m-stream-audio', StreamAudio);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-stream-audio': StreamAudio;
  }
}
