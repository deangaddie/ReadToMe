import { afterEach, beforeEach, describe, expect, it, mock, spyOn } from 'bun:test';
import { html } from 'lit-html';
import { signal } from '@app/core/signals';
// Side-effect import registers the element; a type-only use alone would be elided.
import './measured-list';
import type { MeasuredList } from './measured-list';

interface Row {
  id: string;
  text: string;
}

const rows = (count: number, from = 0): Row[] =>
  Array.from({ length: count }, (_, i) => ({ id: `r${from + i}`, text: `Row ${from + i}` }));

/**
 * happy-dom does no layout, so the spec supplies the two numbers the list reads from it: the
 * viewport's height and each rendered row's height (`heights`, by row key; unmeasured rows are 0
 * and keep their estimate). Real measuring, scrolling and anchoring to the pixel are E2E's.
 */
const VIEWPORT_PX = 400;
const ESTIMATE_PX = 72;
let heights: Map<string, number>;

beforeEach(() => {
  heights = new Map();
  spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (
    this: HTMLElement,
  ) {
    const height = heights.get(this.getAttribute('data-row-key') ?? '') ?? 0;
    return { height } as DOMRect;
  });
});
afterEach(() => {
  mock.restore();
  document.body.replaceChildren();
});

async function mount(items: Row[]) {
  const list = document.createElement('r2m-measured-list') as MeasuredList<Row>;
  Object.defineProperty(list, 'clientHeight', { value: VIEWPORT_PX });
  list.key = (row) => row.id;
  list.row = (row) => html`<p>${row.text}</p>`;
  const tops: number[] = [];
  list.addEventListener('r2m-top-row', (e) => tops.push((e as CustomEvent<number>).detail));
  list.items = items;
  document.body.append(list);
  await list.rendered();
  await list.rendered();
  const scrollTo = async (top: number) => {
    list.scrollTop = top;
    list.dispatchEvent(new Event('scroll'));
    await list.rendered();
  };
  return { list, tops, scrollTo };
}

const rendered = (list: Element) =>
  Array.from(list.querySelectorAll('.r2m-vlist__row')).map((r) => r.getAttribute('data-row-key'));

describe('r2m-measured-list', () => {
  it('renders only the rows near the viewport, each through the row function', async () => {
    const { list } = await mount(rows(1000));
    const keys = rendered(list);
    expect(keys[0]).toBe('r0');
    // The viewport plus the buffer below it, at the estimated height.
    expect(keys.length).toBeGreaterThan(VIEWPORT_PX / ESTIMATE_PX);
    expect(keys.length).toBeLessThan(40);
    expect(list.querySelector('.r2m-vlist__row p')?.textContent).toBe('Row 0');
  });

  it('sizes the spacer to the estimated total', async () => {
    const { list } = await mount(rows(1000));
    expect(list.querySelector<HTMLElement>('.r2m-vlist__spacer')?.style.height).toBe(
      `${1000 * ESTIMATE_PX}px`,
    );
  });

  it('renders nothing for an empty list', async () => {
    const { list, tops } = await mount([]);
    expect(rendered(list)).toEqual([]);
    expect(tops).toEqual([]);
  });

  it('moves the rendered window as the list scrolls', async () => {
    const { list, scrollTo } = await mount(rows(1000));
    await scrollTo(500 * ESTIMATE_PX);
    const keys = rendered(list);
    expect(keys).toContain('r500');
    expect(keys).not.toContain('r0');
    expect(list.querySelector<HTMLElement>('.r2m-vlist__content')?.style.transform).toBe(
      `translateY(${list.offsetOf(Number(keys[0]!.slice(1)))}px)`,
    );
  });

  it('emits r2m-top-row when the row at the top changes, not on every scroll', async () => {
    const { tops, scrollTo } = await mount(rows(1000));
    expect(tops).toEqual([0]);
    await scrollTo(3 * ESTIMATE_PX);
    await scrollTo(3 * ESTIMATE_PX + 10);
    expect(tops).toEqual([0, 3]);
  });

  it('offsetOf uses measured heights where it has them and the estimate elsewhere', async () => {
    heights.set('r0', 100).set('r1', 100);
    const { list } = await mount(rows(1000));
    expect(list.offsetOf(1)).toBe(100);
    expect(list.offsetOf(2)).toBe(200);
    // Unmeasured rows count as the average measured height.
    expect(list.offsetOf(3)).toBe(300);
  });

  it('scrollToIndex scrolls to where the row starts', async () => {
    const { list } = await mount(rows(1000));
    list.scrollToIndex(250);
    expect(list.scrollTop).toBe(list.offsetOf(250));
  });

  it('scrollToOffset scrolls to the pixel', async () => {
    const { list } = await mount(rows(1000));
    list.scrollToOffset(1234);
    expect(list.scrollTop).toBe(1234);
  });

  it('scrollOffset reports the distance from either end', async () => {
    const { list, scrollTo } = await mount(rows(1000));
    Object.defineProperty(list, 'scrollHeight', { value: 1000 * ESTIMATE_PX });
    await scrollTo(1000);
    expect(list.scrollOffset()).toBe(1000);
    expect(list.scrollOffset('bottom')).toBe(1000 * ESTIMATE_PX - VIEWPORT_PX - 1000);
  });

  it('keeps the top row in place when rows are prepended', async () => {
    const { list, scrollTo } = await mount(rows(100));
    await scrollTo(10 * ESTIMATE_PX + 20);
    list.items = [...rows(5, -5), ...rows(100)];
    await list.rendered();
    // r10 is now the 16th row; it stays 20 px above the viewport's top.
    expect(list.scrollTop).toBe(15 * ESTIMATE_PX + 20);
  });

  it('keeps the top row in place when rows above it turn out taller', async () => {
    const { list, scrollTo } = await mount(rows(100));
    await scrollTo(20 * ESTIMATE_PX);
    const top = rendered(list).indexOf('r20');
    expect(top).toBeGreaterThan(0);
    // Every rendered row measures at the estimate except one above the top row.
    for (const key of rendered(list)) heights.set(key!, ESTIMATE_PX);
    heights.set('r19', ESTIMATE_PX + 50);
    list.dispatchEvent(new Event('scroll'));
    list.items = [...list.items];
    await list.rendered();
    expect(list.scrollTop).toBe(list.offsetOf(20));
    expect(list.offsetOf(20) - list.offsetOf(19)).toBe(ESTIMATE_PX + 50);
  });

  it('re-renders rows when the items change', async () => {
    const { list } = await mount(rows(3));
    list.items = [
      { id: 'r0', text: 'Changed' },
      { id: 'r9', text: 'New' },
    ];
    await list.rendered();
    expect(rendered(list)).toEqual(['r0', 'r9']);
    expect(list.querySelector('.r2m-vlist__row p')?.textContent).toBe('Changed');
  });
});

describe('r2m-measured-list row re-measure', () => {
  /** Stands in for the browser's ResizeObserver: the spec fires it for the rows it names. */
  class FakeResizeObserver {
    static instances: FakeResizeObserver[] = [];
    readonly observed = new Set<Element>();
    constructor(readonly callback: ResizeObserverCallback) {
      FakeResizeObserver.instances.push(this);
    }
    observe(target: Element) {
      this.observed.add(target);
    }
    unobserve(target: Element) {
      this.observed.delete(target);
    }
    disconnect() {
      this.observed.clear();
    }
    fire(targets: Element[]) {
      const entries = targets.map((target) => ({ target }) as ResizeObserverEntry);
      this.callback(entries, this as unknown as ResizeObserver);
    }
  }
  const Native = globalThis.ResizeObserver;
  beforeEach(() => {
    FakeResizeObserver.instances = [];
    globalThis.ResizeObserver = FakeResizeObserver as unknown as typeof ResizeObserver;
  });
  afterEach(() => {
    globalThis.ResizeObserver = Native;
  });

  /** The observer that watches rows: the one holding `.r2m-vlist__row` elements. */
  const rowObserver = () =>
    FakeResizeObserver.instances.find((o) =>
      [...o.observed].some((e) => e.classList.contains('r2m-vlist__row')),
    )!;

  it('observes every rendered row and drops rows that left the window', async () => {
    const { list, scrollTo } = await mount(rows(1000));
    const observer = rowObserver();
    expect([...observer.observed].length).toBe(rendered(list).length);
    expect([...observer.observed].every((e) => e.isConnected)).toBe(true);
    await scrollTo(500 * ESTIMATE_PX);
    expect([...observer.observed].every((e) => e.isConnected)).toBe(true);
    expect([...observer.observed].map((e) => e.getAttribute('data-row-key'))).toEqual(
      rendered(list),
    );
  });

  it('a row that grows after its first measure keeps the top row in place', async () => {
    const { list, scrollTo } = await mount(rows(100));
    for (const key of rendered(list)) heights.set(key!, ESTIMATE_PX);
    await scrollTo(20 * ESTIMATE_PX);
    for (const key of rendered(list)) heights.set(key!, ESTIMATE_PX);
    list.items = [...list.items];
    await list.rendered();
    expect(list.scrollTop).toBe(20 * ESTIMATE_PX);

    // A chip wraps in the row above the top one: only that row reports a new size.
    heights.set('r19', ESTIMATE_PX + 50);
    const grown = list.querySelector('[data-row-key="r19"]')!;
    rowObserver().fire([grown]);
    expect(list.offsetOf(20) - list.offsetOf(19)).toBe(ESTIMATE_PX + 50);
    expect(list.scrollTop).toBe(list.offsetOf(20));
  });
});

describe('r2m-measured-list row functions', () => {
  it('re-renders the visible rows when a signal the row function reads changes', async () => {
    const label = signal('before');
    const list = document.createElement('r2m-measured-list') as MeasuredList<Row>;
    Object.defineProperty(list, 'clientHeight', { value: VIEWPORT_PX });
    list.key = (row) => row.id;
    list.row = (row) => html`<p>${row.text} ${label()}</p>`;
    list.items = rows(3);
    document.body.append(list);
    await list.rendered();
    expect(list.querySelector('.r2m-vlist__row p')?.textContent).toBe('Row 0 before');
    label.set('after');
    await list.rendered();
    expect(list.querySelector('.r2m-vlist__row p')?.textContent).toBe('Row 0 after');
  });
});

describe('r2m-measured-list under whole-pixel scrollTop', () => {
  it('does not let rounding compound over repeated prepends', async () => {
    const { list, scrollTo } = await mount(rows(40, 100));
    // The browser keeps scrollTop on whole pixels: emulate it on this instance.
    let stored = 0;
    Object.defineProperty(list, 'scrollTop', {
      get: () => stored,
      set: (v: number) => {
        stored = Math.ceil(v);
      },
    });
    const fractional = 54.171875;
    for (const key of rendered(list)) heights.set(key!, fractional);
    await scrollTo(0);
    list.items = [...list.items];
    await list.rendered();
    expect(list.scrollTop).toBe(0);

    // Three prepends of four rows each, every row a fractional height.
    for (let n = 1; n <= 3; n++) {
      const added = rows(4, 100 - 4 * n);
      for (const row of added) heights.set(row.id, fractional);
      list.items = [...added, ...list.items];
      await list.rendered();
      const anchored = list.offsetOf(list.items.findIndex((r) => r.id === 'r100'));
      expect(Math.abs(list.scrollTop - anchored)).toBeLessThan(1);
    }
  });
});

describe('r2m-measured-list anchor lifetime', () => {
  it('setting the same items again neither re-renders nor leaves an anchor behind', async () => {
    const { list, scrollTo } = await mount(rows(100));
    await scrollTo(10 * ESTIMATE_PX);
    const same = list.items;
    list.items = same;
    await list.rendered();
    // A later user scroll followed by a measure must not snap back to the old position.
    await scrollTo(30 * ESTIMATE_PX);
    for (const key of rendered(list)) heights.set(key!, ESTIMATE_PX + 10);
    list.items = [...same];
    await list.rendered();
    expect(list.scrollTop).toBeGreaterThanOrEqual(30 * ESTIMATE_PX);
  });
});
