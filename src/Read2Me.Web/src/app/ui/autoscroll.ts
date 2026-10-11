import { signal } from '@app/core/signals';

/** How close to the bottom still counts as "at the bottom" (px). */
const BOTTOM_SLACK_PX = 8;

/**
 * Autoscroll that pauses when the user scrolls up (the two stream views, design §7). The host
 * element wires `onScroll` to its scroll container, calls `afterRender()` from `updated()` and
 * shows a "Jump to latest" button while `paused()`.
 */
export class Autoscroll {
  /** True once the user scrolled up; autoscroll resumes after Jump or reaching the bottom. */
  readonly paused = signal(false);

  constructor(private readonly scroller: () => HTMLElement | null) {}

  readonly onScroll = (): void => {
    const el = this.scroller();
    if (!el) return;
    const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - BOTTOM_SLACK_PX;
    this.paused.set(!atBottom);
  };

  readonly jumpToLatest = (): void => {
    this.paused.set(false);
    this.scrollToBottom();
  };

  /** Keeps the newest content in view unless the user is reading further up. */
  afterRender(): void {
    if (!this.paused()) this.scrollToBottom();
  }

  private scrollToBottom(): void {
    const el = this.scroller();
    if (el) el.scrollTop = el.scrollHeight;
  }
}
