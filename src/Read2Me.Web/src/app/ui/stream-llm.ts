import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import type { LlmStreamEvent } from '@app/live/hub-events';
import { Autoscroll } from './autoscroll';
import { type LlmTurn, foldLlmTurns } from './llm-turns';
import { type StatusKind, icon, statusChip } from './partials';

const STATE_VIEW: Record<LlmTurn['state'], { kind: StatusKind; label: string }> = {
  open: { kind: 'busy', label: 'Streaming' },
  completed: { kind: 'ok', label: 'Completed' },
  failed: { kind: 'error', label: 'Failed' },
  aborted: { kind: 'warn', label: 'Aborted' },
};

/** `12 tok · 9.7 tok/s · 1.2 s`, or null when the turn carries no numbers yet. */
export function turnStats(turn: LlmTurn): string | null {
  const parts: string[] = [];
  if (turn.tokensOut != null) parts.push(`${turn.tokensOut} tok`);
  if (turn.tokensPerSecond != null) parts.push(`${turn.tokensPerSecond.toFixed(1)} tok/s`);
  if (turn.generationMs != null) parts.push(`${(turn.generationMs / 1000).toFixed(1)} s`);
  return parts.length ? parts.join(' · ') : null;
}

/**
 * LLM turn list (design §7): one expandable card per request with prompt, thinking and response,
 * escalation/run markers inline, autoscroll that pauses when the user scrolls up. An element
 * because it owns the pause state and the after-render scroll.
 */
export class StreamLlm extends R2mElement {
  #events = signal<readonly LlmStreamEvent[]>([]);
  get events() {
    return this.#events();
  }
  set events(value: readonly LlmStreamEvent[]) {
    this.#events.set(value);
  }

  #maxTurns = signal(50);
  get maxTurns() {
    return this.#maxTurns();
  }
  set maxTurns(value: number) {
    this.#maxTurns.set(value);
  }

  readonly #rows = computed(() => foldLlmTurns(this.#events(), this.#maxTurns()));
  readonly #scroll = new Autoscroll(() =>
    this.querySelector<HTMLElement>('.r2m-stream-llm__scroll'),
  );
  /** True once the user scrolled up; autoscroll resumes after Jump or reaching the bottom. */
  readonly paused = this.#scroll.paused;

  protected override connected(): void {
    this.classList.add('r2m-stream-llm');
  }

  protected override updated(): void {
    this.#scroll.afterRender();
  }

  protected template() {
    const rows = this.#rows();
    return html`
      <div class="r2m-stream-llm__scroll" @scroll=${this.#scroll.onScroll}>
        ${rows.length === 0 ? html`<p class="r2m-stream-llm__empty">No LLM traffic yet.</p>` : nothing}
        ${repeat(
          rows,
          (row) => row.seq,
          (row) =>
            row.kind === 'marker'
              ? html`<div class="r2m-stream-llm__marker">
                  ${icon(row.icon)}<span>${row.text}</span>
                </div>`
              : html`<details class="r2m-stream-llm__turn" data-state=${row.state} open>
                  <summary class="r2m-stream-llm__head">
                    <span class="r2m-stream-llm__preview">${row.paragraphPreview}</span>
                    <span class="r2m-stream-llm__config">${row.configName}</span>
                    ${statusChip({
                      status: STATE_VIEW[row.state].kind,
                      label: STATE_VIEW[row.state].label,
                      compact: true,
                    })}
                    ${
                      turnStats(row)
                        ? html`<span class="r2m-stream-llm__stats">${turnStats(row)}</span>`
                        : nothing
                    }
                  </summary>
                  <div class="r2m-stream-llm__body">
                    <details class="r2m-stream-llm__section">
                      <summary>Prompt</summary>
                      <pre class="r2m-stream-llm__pre">${row.prompt}</pre>
                    </details>
                    ${
                      row.thinking
                        ? html`<details
                            class="r2m-stream-llm__section r2m-stream-llm__section--thinking"
                          >
                            <summary>Thinking</summary>
                            <pre class="r2m-stream-llm__pre">${row.thinking}</pre>
                          </details>`
                        : nothing
                    }
                    <pre class="r2m-stream-llm__pre r2m-stream-llm__response">${row.response}</pre>
                    ${row.reason ? html`<p class="r2m-stream-llm__reason">${row.reason}</p>` : nothing}
                  </div>
                </details>`,
        )}
      </div>
      ${
        this.paused()
          ? html`<button
              type="button"
              class="r2m-button r2m-button--stroked r2m-stream-llm__jump"
              @click=${this.#scroll.jumpToLatest}
            >
              ${icon('arrow_downward')}Jump to latest
            </button>`
          : nothing
      }
    `;
  }
}
define('r2m-stream-llm', StreamLlm);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-stream-llm': StreamLlm;
  }
}
