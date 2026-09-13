import {
  CdkVirtualScrollViewport,
  VIRTUAL_SCROLL_STRATEGY,
  VirtualScrollStrategy,
} from '@angular/cdk/scrolling';
import { Directive, Input, OnDestroy, forwardRef } from '@angular/core';
import { Observable, Subject, distinctUntilChanged } from 'rxjs';
import { HeightIndex } from './height-index';

/** Extra pixels rendered above and below the viewport. */
const BUFFER_PX = 800;
/** First guess for a row before anything is measured. */
const ESTIMATE_PX = 72;
/** Sub-pixel slack when deciding which row is at the top; see `topRow`. */
const TOP_TOLERANCE_PX = 1;
/** Rendered rows carry `data-row-key` so the strategy can measure them. */
export const ROW_KEY_ATTR = 'data-row-key';

interface Anchor {
  key: string;
  /** Pixels between the anchored row's top and the viewport's top. */
  delta: number;
}

/**
 * A virtual scroll strategy for rows of different heights (the CDK ships fixed-size only).
 * Heights are measured from the rendered DOM, remembered by row key and estimated for the rest.
 * Whatever changes — rows prepended, rows above the fold re-measured — the row at the top of the
 * viewport stays where the reader sees it.
 */
export class MeasuredScrollStrategy implements VirtualScrollStrategy {
  private readonly index = new HeightIndex(ESTIMATE_PX);
  private readonly indexChange = new Subject<number>();
  private viewport: CdkVirtualScrollViewport | null = null;
  private resize: ResizeObserver | null = null;
  private pendingAnchor: Anchor | null = null;

  readonly scrolledIndexChange: Observable<number> = this.indexChange.pipe(distinctUntilChanged());

  attach(viewport: CdkVirtualScrollViewport): void {
    this.viewport = viewport;
    if (typeof ResizeObserver !== 'undefined') {
      const host = viewport.getElementRef().nativeElement;
      this.resize = new ResizeObserver(() => this.measure());
      this.resize.observe(host);
      const wrapper = host.querySelector('.cdk-virtual-scroll-content-wrapper');
      if (wrapper) this.resize.observe(wrapper);
    }
    this.update();
  }

  detach(): void {
    this.resize?.disconnect();
    this.resize = null;
    this.viewport = null;
  }

  /** New row keys, set before the viewport sees the matching data. */
  setKeys(keys: readonly string[]): void {
    this.pendingAnchor ??= this.anchor();
    this.index.setKeys(keys);
    // Same-length data never reaches onDataLengthChanged; settle once the pass is over.
    queueMicrotask(() => this.settle());
  }

  onContentScrolled(): void {
    this.update();
  }

  onDataLengthChanged(): void {
    this.settle();
  }

  onContentRendered(): void {
    this.measure();
  }

  onRenderedOffsetChanged(): void {
    // Offsets come from the height index, never from the rendered content.
  }

  scrollToIndex(index: number, behavior: ScrollBehavior): void {
    this.pendingAnchor = null;
    this.viewport?.scrollToOffset(this.index.offsetOf(index), behavior);
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
    const viewport = this.viewport;
    if (!viewport || this.index.length === 0) return null;
    const offset = viewport.measureScrollOffset();
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
    const viewport = this.viewport;
    if (!viewport || !anchor) return;
    const i = this.index.indexOf(anchor.key);
    if (i < 0) return;
    const target = this.index.offsetOf(i) + anchor.delta;
    if (Math.abs(target - viewport.measureScrollOffset()) < 0.5) return;
    this.applyTotalSize();
    viewport.scrollToOffset(target);
  }

  /** Reads rendered row heights, then keeps the top row still. */
  private measure(): void {
    const viewport = this.viewport;
    if (!viewport) return;
    const anchor = this.pendingAnchor ?? this.anchor();
    this.pendingAnchor = null;

    let changed = false;
    const rows = viewport
      .getElementRef()
      .nativeElement.querySelectorAll<HTMLElement>(`[${ROW_KEY_ATTR}]`);
    for (const row of Array.from(rows)) {
      const key = row.getAttribute(ROW_KEY_ATTR);
      const height = row.getBoundingClientRect().height;
      if (key && height > 0 && this.index.setHeight(key, height)) changed = true;
    }
    if (!changed) return;
    this.restore(anchor);
    this.update();
  }

  /**
   * The viewport binds its spacer height through change detection, which runs after this call; a
   * scroll past the old height would be clamped. Size the spacer now, the binding agrees later.
   */
  private applyTotalSize(): void {
    const viewport = this.viewport!;
    const total = this.index.total();
    viewport.setTotalContentSize(total);
    const spacer = viewport
      .getElementRef()
      .nativeElement.querySelector<HTMLElement>('.cdk-virtual-scroll-spacer');
    if (spacer) spacer.style.height = `${total}px`;
  }

  private update(): void {
    const viewport = this.viewport;
    if (!viewport) return;
    const length = Math.min(viewport.getDataLength(), this.index.length);
    viewport.setTotalContentSize(this.index.total());
    if (length === 0) {
      viewport.setRenderedRange({ start: 0, end: 0 });
      viewport.setRenderedContentOffset(0);
      return;
    }
    const offset = viewport.measureScrollOffset();
    const size = viewport.getViewportSize();
    const start = this.index.indexAt(offset - BUFFER_PX);
    const end = Math.min(length, this.index.indexAt(offset + size + BUFFER_PX) + 1);
    viewport.setRenderedRange({ start, end });
    viewport.setRenderedContentOffset(this.index.offsetOf(start));
    this.indexChange.next(this.topRow(offset));
  }
}

/**
 * `<cdk-virtual-scroll-viewport [r2mMeasuredScroll]="keys">`: provides {@link MeasuredScrollStrategy}.
 * `keys` must hold one unique key per row, in row order, and change with the `cdkVirtualForOf` data.
 * Each rendered row must carry `data-row-key` ({@link ROW_KEY_ATTR}).
 */
@Directive({
  selector: 'cdk-virtual-scroll-viewport[r2mMeasuredScroll]',
  exportAs: 'r2mMeasuredScroll',
  providers: [
    {
      provide: VIRTUAL_SCROLL_STRATEGY,
      useFactory: (d: MeasuredScrollDirective) => d.strategy,
      deps: [forwardRef(() => MeasuredScrollDirective)],
    },
  ],
})
export class MeasuredScrollDirective implements OnDestroy {
  readonly strategy = new MeasuredScrollStrategy();

  // A setter, not a signal input: the keys must reach the strategy before the repeater diffs the
  // new rows in the same change detection pass.
  @Input({ required: true })
  set r2mMeasuredScroll(keys: readonly string[]) {
    this.strategy.setKeys(keys);
  }

  ngOnDestroy(): void {
    this.strategy.detach();
  }
}
