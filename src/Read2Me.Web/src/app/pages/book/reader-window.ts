import type { BookStore } from './book-store';

/** Load the adjacent chapter when the viewport is this close to either end. */
export const EDGE_PX = 600;

/** The reader's viewport, in chapters rather than rows. */
export interface ReaderGeometry {
  /** Pixels scrolled from the top. */
  scrollTop(): number;
  /** Pixels left below the viewport. */
  scrollBottom(): number;
  viewportHeight(): number;
  /** Where a loaded chapter's header row starts; `+Infinity` when the chapter is not in the rows. */
  chapterTop(chapterId: string): number;
  scrollTo(offset: number): void;
}

export type ReaderWindowStore = Pick<
  BookStore,
  'window' | 'atEnd' | 'loadNext' | 'loadPrevious' | 'setCurrentChapter'
>;

/**
 * The Book page's reader window policy: which chapters to load as the viewport nears an end,
 * scrolling an opened chapter to the top, and which chapter is current. It acts on the store and
 * the geometry itself; the page polls {@link tick} and forwards the list's top-row events.
 *
 * Two rules keep the chapter the reader is on in place when chapters are short (a part whose only
 * chapter is its heading). Content shorter than the viewport cannot scroll, so a chapter prepended
 * to it pushes the reader's chapter down: load next until there is something to scroll, and
 * previous only then (or when there is no next). And a full window drops its far end only when
 * that chapter is off screen by a margin; dropping one still in view would empty the screen and
 * start the loads over.
 */
export class ReaderWindow {
  /** The chapter last opened from a link or the tree, until the viewport has scrolled it to the top. */
  private scrollTarget: string | null = null;
  /**
   * The chapter last opened from a link or the tree, held current until the reader scrolls: the
   * loads and anchoring that follow an open move the top row on their own, and when the chapters
   * after a short one cannot fill the viewport, the top row is never that chapter at all. Outlives
   * {@link scrollTarget}, which clears once the scroll has landed.
   */
  private openedChapter: string | null = null;

  constructor(
    /** Null while there is no list yet, or it has no rows. */
    private readonly geometry: () => ReaderGeometry | null,
    private readonly store: ReaderWindowStore,
  ) {}

  /**
   * Loads the neighbouring chapter when the viewport nears an end, and scrolls on to an opened
   * chapter that could not reach the top yet once the chapters after it have loaded. Polled rather
   * than tied to scroll events: a chapter shorter than the viewport never scrolls, and wheel input
   * at the very top fires no scroll event at all.
   */
  tick(): void {
    const geometry = this.geometry();
    if (!geometry) return;
    const top = geometry.scrollTop();
    const bottom = geometry.scrollBottom();
    const loaded = this.store.window();
    const scrollable = top + bottom >= 1;

    const reaching = this.reachScrollTarget(geometry, top, bottom);
    if (bottom < EDGE_PX) {
      // Dropping the first chapter is safe when the second starts well above the viewport.
      const second = loaded[1];
      void this.store.loadNext(
        second !== undefined && geometry.chapterTop(second) <= top - EDGE_PX,
      );
    }
    if (!reaching && top < EDGE_PX && (scrollable || this.store.atEnd())) {
      const last = loaded.at(-1);
      void this.store.loadPrevious(
        last !== undefined &&
          geometry.chapterTop(last) >= top + geometry.viewportHeight() + EDGE_PX,
      );
    }
  }

  /** Scrolls an opened chapter to the top and holds it current; nothing when it is not in the rows. */
  open(chapterId: string): void {
    const geometry = this.geometry();
    const target = geometry?.chapterTop(chapterId) ?? Number.POSITIVE_INFINITY;
    if (!geometry || !Number.isFinite(target)) return;
    this.scrollTarget = chapterId;
    this.openedChapter = chapterId;
    geometry.scrollTo(target);
    // The chapter before is needed to scroll up from the top of this one; the tick loads it once
    // that can keep this chapter in place.
    this.tick();
  }

  /**
   * The chapter at the top of the viewport changed; `byReader` when the reader scrolled it there,
   * which ends an open's hold on the current chapter.
   */
  topRow(chapterId: string, byReader: boolean): void {
    if (byReader) this.openedChapter = null;
    if (this.openedChapter) return;
    this.store.setCurrentChapter(chapterId);
  }

  /** Another project: forgets the opened chapter and its scroll target. */
  reset(): void {
    this.scrollTarget = null;
    this.openedChapter = null;
  }

  /**
   * Scrolls on to {@link scrollTarget} when the content below it has grown; true while the target
   * is still out of reach. It is given up once reached, gone, or the book has nothing more to load.
   */
  private reachScrollTarget(geometry: ReaderGeometry, top: number, bottom: number): boolean {
    const chapterId = this.scrollTarget;
    if (!chapterId) return false;
    const target = geometry.chapterTop(chapterId);
    if (!Number.isFinite(target) || top >= target - 1 || this.store.atEnd()) {
      this.scrollTarget = null;
      return false;
    }
    if (bottom >= 1) geometry.scrollTo(target);
    return true;
  }
}
