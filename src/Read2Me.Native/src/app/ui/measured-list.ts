import { html } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { R2mElement, define } from '@app/core/element';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { HeightIndex } from './height-index';

/** Extra pixels rendered above and below the viewport. */
const BUFFER_PX = 800;
/** First guess for a row before anything is measured. */
const ESTIMATE_PX = 72;
/** Sub-pixel slack when deciding which row is at the top; see `topRow`. */
const TOP_TOLERANCE_PX = 1;
/** Rendered rows carry `data-row-key` so the list can measure them. */
export const ROW_KEY_ATTR = 'data-row-key';

adoptStyles(`
  @scope (r2m-measured-list) to (.r2m-vlist__row > *) {
    :scope {
      display: block;
      overflow-y: auto;
      position: relative;
      contain: strict;
      /* The list anchors the top row itself; the browser's own anchoring would fight it. */
      overflow-anchor: none;
    }
    .r2m-vlist__spacer {
      width: 1px;
    }
    .r2m-vlist__content {
      position: absolute;
      inset: 0 0 auto 0;
      will-change: transform;
    }
    /* A row's own box must hold its children's margins, or measured heights miss them. */
    .r2m-vlist__row {
      display: flow-root;
    }
  }
`);

interface Anchor {
  key: string;
  /** Pixels between the anchored row's top and the viewport's top. */
  delta: number;
}

interface Range {
  start: number;
  end: number;
}

/**
 * `<r2m-measured-list>`: a virtual list for rows of different heights — the CDK viewport plus
 * `MeasuredScrollStrategy` in one element. Heights are measured from the rendered DOM, remembered
 * by row key and estimated for the rest. Whatever changes — rows prepended, rows above the fold
 * re-measured — the row at the top of the viewport stays where the reader sees it.
 *
 * Set `key` and `row` before `items`. Emits `r2m-top-row` (detail: index) when the top row changes.
 */
export class MeasuredList<T = unknown> extends R2mElement {
  key: (item: T) => string = String;
  row: (item: T) => unknown = String;

  private readonly index = new HeightIndex(ESTIMATE_PX);
  readonly #items = signal<readonly T[]>([]);
  readonly #range = signal<Range>({ start: 0, end: 0 });
  private pendingAnchor: Anchor | null = null;
  private lastTop = -1;
  private resize: ResizeObserver | null = null;
  private observed: Element | null = null;

  get items(): readonly T[] {
    return this.#items();
  }
  set items(items: readonly T[]) {
    this.pendingAnchor ??= this.anchor();
    this.index.setKeys(items.map(this.key));
    this.#items.set(items);
    this.settle();
  }

  protected override connected(): void {
    const onScroll = () => this.update();
    this.addEventListener('scroll', onScroll, { passive: true });
    this.resize = new ResizeObserver(() => this.measure());
    this.resize.observe(this);
    this.onDisconnect(() => {
      this.removeEventListener('scroll', onScroll);
      this.resize?.disconnect();
      this.resize = null;
      this.observed = null;
    });
  }

  protected template() {
    const { start, end } = this.#range();
    const visible = this.#items().slice(start, end);
    return html`<div class="r2m-vlist__spacer"></div>
      <div class="r2m-vlist__content">
        ${repeat(
          visible,
          this.key,
          (item) =>
            html`<div class="r2m-vlist__row" data-row-key=${this.key(item)}>
              ${this.row(item)}
            </div>`,
        )}
      </div>`;
  }

  protected override updated(): void {
    const content = this.querySelector('.r2m-vlist__content');
    if (content && content !== this.observed) {
      // First render: the content now exists to observe and to size the range against.
      this.observed = content;
      this.resize?.observe(content);
      this.update();
    }
    this.measure();
  }

  /** Where row `index` starts, in pixels from the top of the content (estimated until measured). */
  offsetOf(index: number): number {
    return this.index.offsetOf(index);
  }

  /** Pixels scrolled from the top, or left below the viewport for `'bottom'`. */
  scrollOffset(from: 'top' | 'bottom' = 'top'): number {
    return from === 'top' ? this.scrollTop : this.scrollHeight - this.clientHeight - this.scrollTop;
  }

  scrollToIndex(index: number, behavior: ScrollBehavior = 'auto'): void {
    this.pendingAnchor = null;
    this.applyTotalSize();
    this.scrollTo({ top: this.index.offsetOf(index), behavior });
  }

  scrollToOffset(top: number, behavior: ScrollBehavior = 'auto'): void {
    this.applyTotalSize();
    this.scrollTo({ top, behavior });
  }

  /**
   * The row at the viewport's top edge. Offsets are fractional but the browser keeps `scrollTop` on
   * whole pixels, so a row scrolled to 7551.6 px reports 7551; without the tolerance the row above
   * it — often the previous chapter — would count as the top.
   */
  private topRow(offset: number): number {
    return this.index.indexAt(offset + TOP_TOLERANCE_PX);
  }

  private anchor(): Anchor | null {
    if (!this.isConnected || this.index.length === 0) return null;
    const offset = this.scrollTop;
    const top = this.topRow(offset);
    const key = this.index.keyAt(top);
    return key === undefined ? null : { key, delta: offset - this.index.offsetOf(top) };
  }

  private settle(): void {
    const anchor = this.pendingAnchor;
    this.pendingAnchor = null;
    this.restore(anchor);
    this.update();
  }

  /** Scrolls so the anchored row sits where it was; a row that left the list anchors nothing. */
  private restore(anchor: Anchor | null): void {
    if (!anchor) return;
    const i = this.index.indexOf(anchor.key);
    if (i < 0) return;
    const target = this.index.offsetOf(i) + anchor.delta;
    if (Math.abs(target - this.scrollTop) < 0.5) return;
    this.applyTotalSize();
    this.scrollTop = target;
  }

  /** Reads rendered row heights, then keeps the top row still. */
  private measure(): void {
    const anchor = this.pendingAnchor ?? this.anchor();
    this.pendingAnchor = null;
    let changed = false;
    for (const row of this.querySelectorAll<HTMLElement>(`[${ROW_KEY_ATTR}]`)) {
      const key = row.getAttribute(ROW_KEY_ATTR);
      const height = row.getBoundingClientRect().height;
      if (key && height > 0 && this.index.setHeight(key, height)) changed = true;
    }
    if (!changed) return;
    this.restore(anchor);
    this.update();
  }

  /** Sizes the spacer now, so a scroll past the old total is not clamped. */
  private applyTotalSize(): void {
    const spacer = this.querySelector<HTMLElement>('.r2m-vlist__spacer');
    if (spacer) spacer.style.height = `${this.index.total()}px`;
  }

  private update(): void {
    if (!this.isConnected) return;
    this.applyTotalSize();
    const length = this.index.length;
    const content = this.querySelector<HTMLElement>('.r2m-vlist__content');
    const offset = this.scrollTop;
    let range: Range = { start: 0, end: 0 };
    if (length > 0) {
      const start = this.index.indexAt(offset - BUFFER_PX);
      const end = Math.min(length, this.index.indexAt(offset + this.clientHeight + BUFFER_PX) + 1);
      range = { start, end };
    }
    if (content) content.style.transform = `translateY(${this.index.offsetOf(range.start)}px)`;
    const current = this.#range();
    if (current.start !== range.start || current.end !== range.end) this.#range.set(range);
    const top = length > 0 ? this.topRow(offset) : -1;
    if (top !== this.lastTop) {
      this.lastTop = top;
      this.emit('r2m-top-row', top);
    }
  }
}
define('r2m-measured-list', MeasuredList);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-measured-list': MeasuredList;
  }
}
