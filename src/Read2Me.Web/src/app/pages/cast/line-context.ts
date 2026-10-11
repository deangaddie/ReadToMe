import { html, nothing } from 'lit-html';
import {
  type CharacterLineDto,
  CharactersApi,
  type ContextParagraphDto,
  type ParagraphContextDto,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal, untracked } from '@app/core/signals';
import {
  type ContextWindow,
  INITIAL_WINDOW,
  canGrowAfter,
  canGrowBefore,
  contextSpeakers,
  growAfter,
  growBefore,
} from './context-paging';
import '@app/ui/speaker-chip';

/**
 * A line's surroundings (research §4 "Lines"): the paragraph and its neighbours in the chapter,
 * each with a speaker chip per dialog speaker. Loads on demand; "+ previous" / "+ next" widen the
 * window by the paging steps up to the host's cap. Changing the line resets the window.
 */
export class LineContext extends R2mElement {
  private readonly api = use(CharactersApi);

  readonly #folder = signal('');
  get folder() {
    return this.#folder();
  }
  set folder(value: string) {
    this.#folder.set(value);
  }

  readonly #line = signal<CharacterLineDto | null>(null);
  get line() {
    return this.#line();
  }
  set line(value: CharacterLineDto | null) {
    this.#line.set(value);
  }

  readonly context = signal<ParagraphContextDto | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly contextWindow = signal<ContextWindow>(INITIAL_WINDOW);

  protected override connected(): void {
    this.classList.add('line-context');
    // Tracks the line's identity only, so a reloaded list with the same line keeps its context.
    this.effect(() => {
      const itemId = this.#line()?.itemId;
      untracked(() => {
        if (this.context() === null && itemId === undefined) return;
        this.context.set(null);
        this.error.set(null);
        this.contextWindow.set(INITIAL_WINDOW);
      });
    });
  }

  protected template() {
    const ctx = this.context();
    const error = this.error();
    const loading = this.loading();
    const window = this.contextWindow();
    return html`
      ${loading ? html`<progress aria-label="Loading context"></progress>` : nothing}
      ${error ? html`<p class="line-context__error">${error}</p>` : nothing}
      ${
        ctx
          ? html`${ctx.before.map((p) => this.paragraph(p, false))}
              ${this.paragraph(ctx.paragraph, true)}
              ${ctx.after.map((p) => this.paragraph(p, false))}
              <div class="line-context__more">
                ${
                  canGrowBefore(window)
                    ? html`<button
                        type="button"
                        class="r2m-button"
                        data-action="context-previous"
                        @click=${() => void this.more('before')}
                      >
                        + previous
                      </button>`
                    : nothing
                }
                ${
                  canGrowAfter(window)
                    ? html`<button
                        type="button"
                        class="r2m-button"
                        data-action="context-next"
                        @click=${() => void this.more('after')}
                      >
                        + next
                      </button>`
                    : nothing
                }
              </div>`
          : loading
            ? nothing
            : html`<button
                type="button"
                class="r2m-button"
                data-action="load-context"
                @click=${() => void this.load()}
              >
                Load context
              </button>`
      }
    `;
  }

  private paragraph(p: ContextParagraphDto, query: boolean) {
    const speakers = contextSpeakers(p);
    return html`<div
      class="line-context__paragraph ${query ? 'line-context__paragraph--query' : ''}"
    >
      <div class="line-context__speakers">
        ${
          speakers.length === 0
            ? html`<span class="line-context__narration">—</span>`
            : speakers.map(
                (s) =>
                  html`<r2m-speaker-chip
                    .name=${s.name}
                    .state=${s.unknown ? 'unknown' : 'named'}
                    .compact=${true}
                  ></r2m-speaker-chip>`,
              )
        }
      </div>
      <p class="line-context__text">${p.text}</p>
    </div>`;
  }

  async load(): Promise<void> {
    const line = this.#line();
    if (!line) return;
    const window = this.contextWindow();
    this.loading.set(true);
    this.error.set(null);
    try {
      const ctx = await this.api.context(
        this.#folder(),
        line.chapterId,
        line.paragraphId,
        window.before,
        window.after,
      );
      if (this.#line()?.itemId === line.itemId) this.context.set(ctx);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.loading.set(false);
    }
  }

  async more(side: 'before' | 'after'): Promise<void> {
    this.contextWindow.update((w) => (side === 'before' ? growBefore(w) : growAfter(w)));
    await this.load();
  }
}
define('r2m-line-context', LineContext);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-line-context': LineContext;
  }
}
