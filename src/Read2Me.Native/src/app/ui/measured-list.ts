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
  /**
   * The fractional `scrollTop` the list last set itself. Browsers round the stored value to a
   * whole pixel; anchoring against the intended value instead stops that half-pixel compounding
   * over repeated prepends. Null once the reader scrolls on their own.
   */
  private intendedTop: number | null = null;
  private lastTop = -1;
  private resize: ResizeObserver | null = null;
  /** Watches each rendered row, so a row that changes height after its first measure (a chip
   * wrapping, an editor opening) re-measures on its own and the top row stays put (spec §7). */
  private rowResize: ResizeObserver | null = null;
  private readonly observedRows = new Set<Element>();
  private sized = false;

  get items(): readonly T[] {
    return this.#items();
  }
  set items(items: readonly T[]) {
    // The same array again would not re-render, so the anchor it captured would go stale.
    if (items === this.#items()) return;
    this.pendingAnchor ??= this.anchor();
    this.index.setKeys(items.map(this.key));
    this.#items.set(items);
    this.settle();
  }

  protected override connected(): void {
    const onScroll = () => {
      if (this.intendedTop !== null && Math.abs(this.intendedTop - this.scrollTop) >= 1) {
        // The reader scrolled: neither the intended offset nor a pending anchor describes where they are.
        this.intendedTop = null;
        this.pendingAnchor = null;
      }
      this.update();
    };
    this.addEventListener('scroll', onScroll, { passive: true });
    this.resize = new ResizeObserver(() => this.measure());
    this.resize.observe(this);
    this.rowResize = new ResizeObserver((entries) => this.measureRows(entries));
    this.onDisconnect(() => {
      this.removeEventListener('scroll', onScroll);
      this.resize?.disconnect();
      this.resize = null;
      this.rowResize?.disconnect();
      this.rowResize = null;
      this.observedRows.clear();
      this.sized = false;
    });
  }

  protected template() {
    const { start, end } = this.#range();
    // Row templates are built here, inside the effect, not in the repeat callback (which lit runs
    // at commit time, outside it): a signal a row reads then re-renders the visible rows.
    const rows = this.#items()
      .slice(start, end)
      .map((item) => ({ key: this.key(item), content: this.row(item) }));
    return html`<div class="r2m-vlist__spacer"></div>
      <div class="r2m-vlist__content">
        ${repeat(
          rows,
          (row) => row.key,
          (row) => html`<div class="r2m-vlist__row" data-row-key=${row.key}>${row.content}</div>`,
        )}
      </div>`;
  }

  protected override updated(): void {
    if (!this.sized) {
      // First render: the content now exists to size the range against.
      this.sized = true;
      this.update();
    }
    this.measure();
    this.observeRows();
  }

  /** Keeps the row observer on exactly the rows the last render left in the DOM. */
  private observeRows(): void {
    const observer = this.rowResize;
    if (!observer) return;
    const current = new Set<Element>(this.querySelectorAll(`[${ROW_KEY_ATTR}]`));
    for (const row of this.observedRows) {
      if (!current.has(row)) {
        observer.unobserve(row);
        this.observedRows.delete(row);
      }
    }
    for (const row of current) {
      if (!this.observedRows.has(row)) {
        observer.observe(row);
        this.observedRows.add(row);
      }
    }
  }

  /** One or a few rows changed height on their own: record them, then keep the top row still. */
  private measureRows(entries: ResizeObserverEntry[]): void {
    const anchor = this.pendingAnchor ?? this.anchor();
    this.pendingAnchor = null;
    let changed = false;
    for (const { target } of entries) {
      const key = target.getAttribute(ROW_KEY_ATTR);
      const height = target.getBoundingClientRect().height;
      if (key && height > 0 && this.index.setHeight(key, height)) changed = true;
    }
    if (!changed) return;
    this.restore(anchor);
    this.update();
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
    this.scrollToOffset(this.index.offsetOf(index), behavior);
  }

  scrollToOffset(top: number, behavior: ScrollBehavior = 'auto'): void {
    this.pendingAnchor = null;
    this.applyTotalSize();
    this.intendedTop = top;
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

  /** `scrollTop` as the list meant it, when the browser only rounded it; else as it is. */
  private scrollOffsetIntended(): number {
    const actual = this.scrollTop;
    const intended = this.intendedTop;
    return intended !== null && Math.abs(intended - actual) < 1 ? intended : actual;
  }

  private anchor(): Anchor | null {
    if (!this.isConnected || this.index.length === 0) return null;
    const offset = this.scrollOffsetIntended();
    const top = this.topRow(offset);
    const key = this.index.keyAt(top);
    return key === undefined ? null : { key, delta: offset - this.index.offsetOf(top) };
  }

  /**
   * Restores the anchor against the estimated offsets now, and keeps it for the measure that
   * follows the render: the corrected restore then starts from the original delta rather than
   * from a `scrollTop` the browser has already rounded, so the two passes round only once.
   */
  private settle(): void {
    this.restore(this.pendingAnchor);
    this.update();
  }

  /** Scrolls so the anchored row sits where it was; a row that left the list anchors nothing. */
  private restore(anchor: Anchor | null): void {
    if (!anchor) return;
    const i = this.index.indexOf(anchor.key);
    if (i < 0) return;
    const target = this.index.offsetOf(i) + anchor.delta;
    if (Math.abs(target - this.scrollOffsetIntended()) < 0.5) return;
    this.applyTotalSize();
    this.intendedTop = target;
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
    const top = length > 0 ? this.topRow(this.scrollOffsetIntended()) : -1;
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
