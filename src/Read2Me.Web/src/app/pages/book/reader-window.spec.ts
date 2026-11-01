import { beforeEach, describe, expect, it } from 'bun:test';
import { EDGE_PX, ReaderWindow, type ReaderGeometry } from './reader-window';

/** A static geometry: each spec sets the offsets and chapter tops; `scrollTo` only records. */
class FakeGeometry implements ReaderGeometry {
  top = 0;
  bottom = 0;
  viewport = 800;
  tops: Record<string, number> = {};
  readonly scrolls: number[] = [];

  scrollTop(): number {
    return this.top;
  }
  scrollBottom(): number {
    return this.bottom;
  }
  viewportHeight(): number {
    return this.viewport;
  }
  chapterTop(chapterId: string): number {
    return this.tops[chapterId] ?? Number.POSITIVE_INFINITY;
  }
  scrollTo(offset: number): void {
    this.scrolls.push(offset);
  }
}

/** A recording store: the spec sets the window and `atEnd`; loads and current-chapter calls are kept. */
class FakeStore {
  loaded: string[] = [];
  end = false;
  readonly next: boolean[] = [];
  readonly previous: boolean[] = [];
  readonly current: string[] = [];

  window = (): string[] => this.loaded;
  atEnd = (): boolean => this.end;
  loadNext = (dropFar = true): Promise<void> => {
    this.next.push(dropFar);
    return Promise.resolve();
  };
  loadPrevious = (dropFar = true): Promise<void> => {
    this.previous.push(dropFar);
    return Promise.resolve();
  };
  setCurrentChapter = (chapterId: string): void => {
    this.current.push(chapterId);
  };
}

describe('ReaderWindow', () => {
  let geometry: FakeGeometry;
  let store: FakeStore;
  let present: boolean;
  let reader: ReaderWindow;

  beforeEach(() => {
    geometry = new FakeGeometry();
    store = new FakeStore();
    present = true;
    reader = new ReaderWindow(() => (present ? geometry : null), store);
  });

  /** Chapters stacked from offset 0, each `height` px tall. */
  function chapters(height: number, ...ids: string[]): void {
    store.loaded = ids;
    geometry.tops = Object.fromEntries(ids.map((id, i) => [id, i * height]));
  }

  describe('with no geometry', () => {
    it('does nothing on a tick', () => {
      present = false;
      store.loaded = ['c1'];
      reader.tick();
      expect(store.next).toEqual([]);
      expect(store.previous).toEqual([]);
    });

    it('opens nothing', () => {
      present = false;
      reader.open('c1');
      expect(geometry.scrolls).toEqual([]);
    });
  });

  describe('loading next', () => {
    it('loads next when the bottom is within EDGE_PX', () => {
      chapters(5000, 'c1', 'c2');
      geometry.top = 5000;
      geometry.bottom = EDGE_PX - 1;
      reader.tick();
      expect(store.next).toHaveLength(1);
    });

    it('does not load next with EDGE_PX or more below', () => {
      chapters(5000, 'c1', 'c2');
      geometry.top = 5000;
      geometry.bottom = EDGE_PX;
      reader.tick();
      expect(store.next).toEqual([]);
    });

    it('drops the first chapter when the second starts EDGE_PX above the viewport', () => {
      chapters(1000, 'c1', 'c2');
      geometry.top = 1000 + EDGE_PX;
      geometry.bottom = 0;
      reader.tick();
      expect(store.next).toEqual([true]);
    });

    it('keeps the first chapter while the second starts less than EDGE_PX above the viewport', () => {
      chapters(1000, 'c1', 'c2');
      geometry.top = 1000 + EDGE_PX - 1;
      geometry.bottom = 0;
      reader.tick();
      expect(store.next).toEqual([false]);
    });

    it('keeps the only chapter', () => {
      chapters(1000, 'c1');
      geometry.top = 5000;
      geometry.bottom = 0;
      reader.tick();
      expect(store.next).toEqual([false]);
    });
  });

  describe('loading previous', () => {
    it('loads previous when the top is within EDGE_PX and the content can scroll', () => {
      chapters(5000, 'c1', 'c2');
      geometry.top = EDGE_PX - 1;
      geometry.bottom = 5000;
      reader.tick();
      expect(store.previous).toHaveLength(1);
      expect(store.next).toEqual([]);
    });

    it('does not load previous with EDGE_PX or more above', () => {
      chapters(5000, 'c1', 'c2');
      geometry.top = EDGE_PX;
      geometry.bottom = 5000;
      reader.tick();
      expect(store.previous).toEqual([]);
    });

    it('drops the last chapter when it starts EDGE_PX below the viewport', () => {
      chapters(600, 'c1', 'c2', 'c3', 'c4');
      geometry.viewport = 800;
      geometry.top = 1800 - 800 - EDGE_PX; // c4 at 1800 = top + viewport + EDGE_PX
      geometry.bottom = 5000;
      reader.tick();
      expect(store.previous).toEqual([true]);
    });

    it('keeps the last chapter while it starts less than EDGE_PX below the viewport', () => {
      chapters(600, 'c1', 'c2', 'c3', 'c4');
      geometry.viewport = 800;
      geometry.top = 1800 - 800 - EDGE_PX + 1;
      geometry.bottom = 5000;
      reader.tick();
      expect(store.previous).toEqual([false]);
    });

    it('keeps the window when nothing is loaded', () => {
      store.loaded = [];
      geometry.top = 0;
      geometry.bottom = 5000;
      reader.tick();
      expect(store.previous).toEqual([false]);
    });
  });

  describe('next before previous', () => {
    it('loads only next while the content cannot scroll', () => {
      chapters(300, 'c1');
      geometry.top = 0;
      geometry.bottom = 0.5;
      reader.tick();
      expect(store.next).toEqual([false]);
      expect(store.previous).toEqual([]);
    });

    it('loads previous on content that cannot scroll once the store is at the end', () => {
      chapters(300, 'c1');
      store.end = true;
      geometry.top = 0;
      geometry.bottom = 0;
      reader.tick();
      expect(store.previous).toEqual([false]);
    });

    it('loads both in one tick when the content can scroll', () => {
      chapters(300, 'c1', 'c2');
      geometry.top = 100;
      geometry.bottom = 100;
      reader.tick();
      expect(store.next).toHaveLength(1);
      expect(store.previous).toHaveLength(1);
    });
  });

  describe('scroll target', () => {
    /** c1 0–600, c2 600–1200 (opened), c3 from 1200; the viewport reaches c2's top. */
    function openable(): void {
      chapters(600, 'c1', 'c2', 'c3');
      geometry.top = 0;
      geometry.bottom = 5000;
    }

    it('open scrolls to the chapter top and ticks at once', () => {
      openable();
      geometry.bottom = 0;
      reader.open('c2');
      expect(geometry.scrolls[0]).toBe(600);
      expect(store.next).toHaveLength(1);
    });

    it('open of a chapter not in the rows does nothing', () => {
      openable();
      reader.open('c9');
      expect(geometry.scrolls).toEqual([]);
      expect(store.previous).toEqual([]);
      reader.topRow('c1', false);
      expect(store.current).toEqual(['c1']);
    });

    it('scrolls again each tick while out of reach, and holds previous back', () => {
      openable();
      reader.open('c2');
      reader.tick();
      expect(geometry.scrolls).toEqual([600, 600, 600]);
      expect(store.previous).toEqual([]);
    });

    it('does not scroll while nothing is left below', () => {
      openable();
      geometry.bottom = 0.5;
      reader.open('c2');
      expect(geometry.scrolls).toEqual([600]);
      expect(store.previous).toEqual([]);
    });

    it('gives up once reached', () => {
      openable();
      reader.open('c2');
      geometry.top = 599;
      reader.tick();
      expect(geometry.scrolls).toEqual([600, 600]);
      expect(store.previous).toEqual([false]);
      geometry.top = 0;
      reader.tick();
      expect(geometry.scrolls).toEqual([600, 600]);
    });

    it('gives up once the chapter is gone', () => {
      openable();
      reader.open('c2');
      delete geometry.tops['c2'];
      reader.tick();
      expect(geometry.scrolls).toEqual([600, 600]);
      expect(store.previous).toEqual([false]);
    });

    it('gives up once the store is at the end', () => {
      openable();
      reader.open('c2');
      store.end = true;
      reader.tick();
      expect(geometry.scrolls).toEqual([600, 600]);
      expect(store.previous).toEqual([false]);
    });
  });

  describe('current chapter', () => {
    it('sets the top row current while nothing is opened', () => {
      reader.topRow('c1', false);
      expect(store.current).toEqual(['c1']);
    });

    it('holds the opened chapter through loads and anchoring', () => {
      chapters(600, 'c1', 'c2');
      reader.open('c2');
      reader.topRow('c1', false);
      expect(store.current).toEqual([]);
    });

    it('releases the hold when the reader scrolls', () => {
      chapters(600, 'c1', 'c2');
      reader.open('c2');
      reader.topRow('c1', true);
      reader.topRow('c2', false);
      expect(store.current).toEqual(['c1', 'c2']);
    });

    it('holds the opened chapter after the scroll target is given up', () => {
      chapters(600, 'c1', 'c2');
      reader.open('c2');
      store.end = true;
      reader.tick();
      reader.topRow('c1', false);
      expect(store.current).toEqual([]);
    });

    it('reset clears the hold and the target', () => {
      chapters(600, 'c1', 'c2');
      geometry.bottom = 5000;
      reader.open('c2');
      reader.reset();
      reader.topRow('c1', false);
      expect(store.current).toEqual(['c1']);
      reader.tick();
      expect(geometry.scrolls).toEqual([600, 600]);
      expect(store.previous).toEqual([false]);
    });
  });
});
