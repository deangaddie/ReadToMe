import { afterEach, beforeEach, describe, expect, it, mock, spyOn } from 'bun:test';
import { html } from 'lit-html';
// Side-effect import registers the element; a type-only use alone would be elided.
import '@app/ui/measured-list';
import type { MeasuredList } from '@app/ui/measured-list';
import { ReaderWindow, listGeometry } from './reader-window';

/**
 * A deep link holding its chapter header at the top while the chapters before it are prepended
 * (the anchoring maths behind `BigBookTests`' deep link): the real `<r2m-measured-list>` and the
 * real {@link ReaderWindow}, joined by {@link listGeometry} as the Book page joins them, over fake
 * geometry. happy-dom has no layout, so the spec supplies each row's height (fractional, as a
 * browser measures text) and a `scrollTop` the browser keeps on whole pixels. Real layout is
 * `BigBookTests`' one remaining case.
 */

interface Row {
  kind: 'chapter' | 'paragraph';
  key: string;
  chapterId: string;
}

const VIEWPORT_PX = 400;
const HEADER_PX = 61.4375;
const PARAGRAPH_PX = 54.171875;
/** Chapters c1..c6, each a header and three paragraphs, so a chapter is shorter than the viewport. */
const CHAPTERS = ['c1', 'c2', 'c3', 'c4', 'c5', 'c6'];

function chapterRows(chapterId: string): Row[] {
  return [
    { kind: 'chapter', key: `chapter:${chapterId}`, chapterId },
    ...[1, 2, 3].map((n) => ({
      kind: 'paragraph' as const,
      key: `paragraph:${chapterId}-${n}`,
      chapterId,
    })),
  ];
}

const heightOf = (key: string) => (key.startsWith('chapter:') ? HEADER_PX : PARAGRAPH_PX);

/**
 * The store's window, as `BookStore` keeps it: next and previous load one chapter each, and the
 * list's items are rebuilt from it the way the Book page's `rows` are.
 */
class FakeBook {
  chapters: string[];
  readonly current: string[] = [];

  constructor(
    readonly list: MeasuredList<Row>,
    opened: string,
  ) {
    this.chapters = [opened];
    this.render();
  }

  rows(): Row[] {
    return this.chapters.flatMap(chapterRows);
  }

  render(): void {
    this.list.items = this.rows();
  }

  window = (): string[] => this.chapters;
  atEnd = (): boolean => this.chapters.at(-1) === CHAPTERS.at(-1);
  loadNext = (): Promise<void> => this.extend(1);
  loadPrevious = (): Promise<void> => this.extend(-1);
  setCurrentChapter = (chapterId: string): void => {
    this.current.push(chapterId);
  };

  private extend(step: 1 | -1): Promise<void> {
    const edge = CHAPTERS.indexOf((step === 1 ? this.chapters.at(-1) : this.chapters[0])!);
    const next = CHAPTERS[edge + step];
    if (next) {
      this.chapters = step === 1 ? [...this.chapters, next] : [next, ...this.chapters];
      this.render();
    }
    return Promise.resolve();
  }
}

beforeEach(() => {
  spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (
    this: HTMLElement,
  ) {
    const key = this.getAttribute('data-row-key');
    return { height: key ? heightOf(key) : 0 } as DOMRect;
  });
});
afterEach(() => {
  mock.restore();
  document.body.replaceChildren();
});

/** Mounts the list with the browser's two habits: whole-pixel `scrollTop`, clamped to the content. */
async function mount(opened: string) {
  const list = document.createElement('r2m-measured-list') as MeasuredList<Row>;
  Object.defineProperty(list, 'clientHeight', { value: VIEWPORT_PX });
  Object.defineProperty(list, 'scrollHeight', {
    get: () => list.offsetOf(list.items.length),
  });
  let stored = 0;
  Object.defineProperty(list, 'scrollTop', {
    get: () => stored,
    set: (v: number) => {
      stored = Math.max(0, Math.min(Math.ceil(v), list.scrollHeight - VIEWPORT_PX));
    },
  });
  list.key = (row) => row.key;
  list.row = (row) => html`<p>${row.key}</p>`;
  const book = new FakeBook(list, opened);
  const reader = new ReaderWindow(() => listGeometry(list, book.rows()), book);
  // The Book page's top-row forwarding: the row's chapter, and whether the reader scrolled it.
  const forward = (byReader: boolean) => (e: Event) => {
    const row = book.rows()[(e as CustomEvent<number>).detail];
    if (row) reader.topRow(row.chapterId, byReader);
  };
  list.addEventListener('r2m-top-row', forward(false));
  list.addEventListener('r2m-reader-scroll', forward(true));
  document.body.append(list);
  await list.rendered();
  await list.rendered();
  return { list, book, reader };
}

/** One poll of the reader window, then the render and measure its loads cause. */
async function tick(list: MeasuredList<Row>, reader: ReaderWindow) {
  reader.tick();
  await list.rendered();
  await list.rendered();
}

/** The header's top relative to the viewport's top. */
function headerOffset(list: MeasuredList<Row>, chapterId: string): number {
  const index = list.items.findIndex((r) => r.key === `chapter:${chapterId}`);
  return list.offsetOf(index) - list.scrollTop;
}

/** Ticks until `chapterId` is the first chapter in the window. */
async function prependTo(
  list: MeasuredList<Row>,
  book: FakeBook,
  reader: ReaderWindow,
  chapterId: string,
) {
  for (let n = 0; n < 20 && book.chapters[0] !== chapterId; n++) await tick(list, reader);
  expect(book.chapters[0]).toBe(chapterId);
}

describe('a deep link over prepends', () => {
  it('lands the opened header at the top and holds it within a pixel over three prepends', async () => {
    const { list, book, reader } = await mount('c4');
    reader.open('c4');
    await list.rendered();

    // The opened chapter alone cannot scroll, so the chapters after it load first; only then do
    // c3, c2 and c1 prepend, one per tick.
    const offsets: number[] = [];
    for (let n = 0; n < 20 && book.chapters[0] !== 'c1'; n++) {
      const first = book.chapters[0];
      await tick(list, reader);
      if (book.chapters[0] !== first) offsets.push(headerOffset(list, 'c4'));
    }

    expect(book.chapters[0]).toBe('c1');
    expect(offsets).toHaveLength(3);
    for (const offset of offsets) expect(Math.abs(offset)).toBeLessThan(1);
  });

  it('holds the last chapter current when its header cannot reach the top', async () => {
    // Nothing follows c6, so once the chapters before it fill the viewport the list stops at the
    // end with c6's header below the top: the top row belongs to an earlier chapter.
    const { list, book, reader } = await mount('c6');
    reader.open('c6');
    await prependTo(list, book, reader, 'c3');

    expect(headerOffset(list, 'c6')).toBeGreaterThan(1);
    expect(book.rows()[list.topIndex]?.chapterId).not.toBe('c6');
    // c6 was current from the first top row; the top rows the prepends brought never replace it.
    expect(book.current).toEqual(['c6']);
  });

  it('lets the reader take over: a scroll they make moves the current chapter', async () => {
    const { list, book, reader } = await mount('c4');
    reader.open('c4');
    await prependTo(list, book, reader, 'c1');

    list.scrollTop = list.offsetOf(list.items.findIndex((r) => r.key === 'chapter:c2'));
    list.dispatchEvent(new Event('scroll'));
    await list.rendered();

    expect(book.current.at(-1)).toBe('c2');
  });
});

describe('listGeometry', () => {
  it('reads a chapter top from its header row and calls a chapter not in the rows unreachable', async () => {
    const { list, book } = await mount('c4');
    await book.loadNext();
    await list.rendered();
    const geometry = listGeometry(list, book.rows());

    expect(geometry.chapterTop('c4')).toBe(0);
    expect(geometry.chapterTop('c5')).toBe(HEADER_PX + 3 * PARAGRAPH_PX);
    expect(geometry.chapterTop('c1')).toBe(Number.POSITIVE_INFINITY);
    expect(geometry.viewportHeight()).toBe(VIEWPORT_PX);
  });
});
