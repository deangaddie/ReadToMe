import { html } from 'lit-html';
import { signal } from '@app/core/signals';
import { icon } from './partials';

/** How close to the bottom still counts as "at the bottom" (px). */
const BOTTOM_SLACK_PX = 8;

const isAtBottom = (el: HTMLElement): boolean =>
  el.scrollTop + el.clientHeight >= el.scrollHeight - BOTTOM_SLACK_PX;

/**
 * Autoscroll that pauses when the user scrolls up or presses the control (the two stream views,
 * design §7). The host element puts {@link scrollControl} above its scroll container, wires
 * `onScroll` and `onContentVisibilityChange` to the container, marks it `data-following` while not
 * `paused()` and calls `afterRender()` from `updated()`.
 */
export class Autoscroll {
  /** True while held; resumes through the control or when the user returns to the bottom. */
  readonly paused = signal(false);

  /** Where the scroller was last seen, so a scroll event says which way it moved. */
  #lastTop = 0;
  /** Whether it was last seen short of the bottom. */
  #away = false;

  constructor(private readonly scroller: () => HTMLElement | null) {}

  /**
   * Only a move pauses or resumes: up and away from the bottom pauses; arriving at it, or moving
   * down within it, resumes. An event that finds the view where it was left (the late one for our
   * own scroll, after a press on Pause) changes nothing, nor does content growing below.
   */
  readonly onScroll = (): void => {
    const el = this.scroller();
    if (!el) return;
    const moved = el.scrollTop - this.#lastTop;
    if (moved === 0) return;
    const atBottom = isAtBottom(el);
    if (atBottom && (moved > 0 || this.#away)) this.paused.set(false);
    else if (!atBottom && moved < 0) this.paused.set(true);
    this.#lastTop = el.scrollTop;
    this.#away = !atBottom;
  };

  /**
   * An entry under `content-visibility: auto` swaps its estimated height for its real one after the
   * scroll that brought it near, so the bottom moves. A capture listener: the event is the entry's.
   * A view already moved up and away is the user's (a key, a focus scroll) with its `scroll` event
   * still to come, so it is left for `onScroll`.
   */
  readonly onContentVisibilityChange = {
    handleEvent: (): void => {
      const el = this.scroller();
      if (el && el.scrollTop < this.#lastTop && !isAtBottom(el)) return;
      this.afterRender();
    },
    capture: true,
  };

  readonly toggle = (): void => {
    if (!this.paused()) {
      this.paused.set(true);
      return;
    }
    this.paused.set(false);
    this.scrollToBottom();
  };

  /** Keeps the newest content in view unless paused. */
  afterRender(): void {
    if (!this.paused()) this.scrollToBottom();
  }

  private scrollToBottom(): void {
    const el = this.scroller();
    if (!el) return;
    el.scrollTop = el.scrollHeight;
    this.#lastTop = el.scrollTop;
    this.#away = false;
  }
}

/**
 * A stream's header with its one scrolling control (WCAG 2.2.2). The label says what a press does,
 * so there is no `aria-pressed`. `base` is the stream's class, `r2m-stream-llm`.
 */
export function scrollControl(scroll: Autoscroll, base: string) {
  const paused = scroll.paused();
  return html`<div class="${base}__bar">
    <button type="button" class="r2m-button ${base}__follow" @click=${scroll.toggle}>
      ${icon(paused ? 'arrow_downward' : 'pause')}${paused ? 'Jump to latest' : 'Pause scrolling'}
    </button>
  </div>`;
}
