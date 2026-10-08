import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { BookOverviewDto, ParagraphDto, ProjectDetailDto } from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { LiveSnapshot, Receipt } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import type { ProjectStore } from '../project/project-store';
import { BookStore, RECEIPT_BATCH_MS, WINDOW_CAP } from './book-store';
import type { TreeNode } from './book-tree';

const BASE = '/api/projects/dune';

const DETAIL = {
  narrator: { characterId: 'n', displayName: 'Narrator', isLinked: false },
} as ProjectDetailDto;

const OVERVIEW: BookOverviewDto = {
  hasContent: true,
  volumes: [{ id: 'v1', title: null }],
  characters: [{ id: 'h', name: 'Hardin', aliases: [] }],
  totalParts: 2,
  totalChapters: 3,
};

function paragraphs(...ids: string[]): { paragraphs: ParagraphDto[] } {
  return {
    paragraphs: ids.map((id) => ({
      id,
      isPauseParagraph: false,
      items: [
        {
          id: `${id}-i`,
          itemType: 'Character',
          text: id,
          characterId: null,
          audioFileName: null,
          voiceInstructions: null,
          orderKey: 'a',
          isPause: false,
        },
      ],
    })),
  };
}

function receipt(
  revision: number,
  facets: string,
  effects: Partial<Receipt['effects']> = {},
  isOwn = false,
): Receipt {
  return {
    folder: 'dune',
    mutationName: 'X',
    mutationId: 'm',
    revision,
    originId: 'o',
    isOwn,
    effects: {
      scope: 'Exact',
      facets,
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
      ...effects,
    },
  };
}

/** The receipt and resync streams the store subscribes to. */
class FakeBookLive {
  readonly receiptStream = new Emitter<Receipt>();
  readonly resyncStream = new Emitter<LiveSnapshot>();
  receipts(_folder: string, listener: (r: Receipt) => void) {
    return this.receiptStream.subscribe(listener);
  }
  resynced(listener: (s: LiveSnapshot) => void) {
    return this.resyncStream.subscribe(listener);
  }
}

const chapterList = (ids: string[]) => ({
  chapters: ids.map((id) => ({ id, title: id.toUpperCase() })),
});

/**
 * The Angular spec answered requests one by one through HttpTestingController; here the routes
 * are canned up front (`FakeApi`) and the spec reads what was asked afterwards. The book is one
 * volume with two parts: p1 → c1, c2; p2 → c3.
 */
describe('BookStore', () => {
  let api: FakeApi;
  let live: FakeBookLive;
  let store: BookStore;
  let toasts: string[];
  let projectStatus: ReturnType<typeof signal<object | null>>;
  let projectRevision: ReturnType<typeof signal<number>>;

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));
  const calls = (path: string) => api.calls('GET', `${BASE}${path}`).length;

  beforeEach(() => {
    resetServices();
    api = new FakeApi();
    api.install();
    live = new FakeBookLive();
    override(LiveService, live as unknown as LiveService);
    toasts = [];
    override(ToastService, { info: (m: string) => toasts.push(m) } as unknown as ToastService);
    projectStatus = signal<object | null>({});
    projectRevision = signal(5);
    const project = {
      revision: projectRevision,
      status: projectStatus,
      detail: signal(DETAIL),
    } as unknown as ProjectStore;
    store = new BookStore(project);

    api
      .on('GET', `${BASE}/book`, OVERVIEW)
      .on('GET', `${BASE}/audio/reviews`, {})
      .on('GET', `${BASE}/nodes/volume/v1/children`, {
        parts: [
          { id: 'p1', title: 'One' },
          { id: 'p2', title: 'Two' },
        ],
      })
      .on('GET', `${BASE}/nodes/part/p1/children`, chapterList(['c1', 'c2']))
      .on('GET', `${BASE}/nodes/part/p2/children`, { chapters: [{ id: 'c3', title: null }] })
      .on('GET', `${BASE}/nodes/chapter/c1/children`, paragraphs('a', 'b'))
      .on('GET', `${BASE}/nodes/chapter/c2/children`, paragraphs('c'))
      .on('GET', `${BASE}/nodes/chapter/c3/children`, paragraphs('d'));
  });

  afterEach(() => store.close());

  function partNode(id: string): TreeNode {
    return {
      id,
      level: 'part',
      title: id,
      rawTitle: id,
      isFirst: true,
      isLast: true,
      expandable: true,
      loaded: false,
      loadTarget: { level: 'part', id },
      children: [],
    };
  }

  async function expand(part: string) {
    store.setExpanded(partNode(part), true);
    await settle();
  }

  it('opens: overview, reviews, and the single volume collapses to its parts', async () => {
    await store.open('dune');
    expect(calls('/book')).toBe(1);
    expect(calls('/audio/reviews')).toBe(1);
    expect(store.tree().map((n) => `${n.level}:${n.title}`)).toEqual(['part:One', 'part:Two']);
    expect(store.speakers().names).toEqual({ h: 'Hardin' });
  });

  it('openFirstChapter while the volume is still loading waits for that read instead of re-asking', async () => {
    let answer!: (parts: unknown) => void;
    const held = new Promise((r) => (answer = r));
    api.on('GET', `${BASE}/nodes/volume/v1/children`, () => held);
    const opened = store.open('dune');
    await settle();
    // The volume read is in flight; a concurrent first-chapter request must share it.
    const first = store.openFirstChapter();
    await settle();
    answer({ parts: [{ id: 'p1', title: 'One' }] });
    await opened;
    await first;
    expect(calls('/nodes/volume/v1/children')).toBe(1);
    expect(store.window()).toEqual(['c1']);
  });

  it('a children read that leaves the node unloaded ends the search instead of looping', async () => {
    await store.open('dune');
    const first = store.openFirstChapter();
    // Closing mid-read: the store forgets the folder, the read is discarded, and the search ends.
    store.close();
    expect(await first).toBeUndefined();
    expect(store.window()).toEqual([]);
  });

  it('openFirstChapter loads structure down to the first chapter and asks the reader to scroll', async () => {
    await store.open('dune');
    await store.openFirstChapter();
    expect(store.window()).toEqual(['c1']);
    expect(store.chapters()).toEqual([
      expect.objectContaining({ id: 'c1', title: 'C1', paragraphs: expect.any(Array) }),
    ]);
    expect(store.scrollRequest()).toMatchObject({ chapterId: 'c1' });
  });

  it('loadNext crosses a part boundary, loading the next part on demand', async () => {
    await store.open('dune');
    await store.openFirstChapter();

    await store.loadNext();
    expect(store.window()).toEqual(['c1', 'c2']);
    expect(calls('/nodes/part/p2/children')).toBe(0);

    await store.loadNext();
    expect(calls('/nodes/part/p2/children')).toBe(1);
    expect(store.window()).toEqual(['c1', 'c2', 'c3']);
    expect(store.atEnd()).toBe(true);

    await store.loadPrevious();
    expect(store.window()).toEqual(['c1', 'c2', 'c3']);
  });

  it('atEnd is false while a later part is unread', async () => {
    await store.open('dune');
    await store.openFirstChapter();
    await store.loadNext();
    // c2 ends p1, but p2 has not been read: there may be more.
    expect(store.atEnd()).toBe(false);
  });

  it('a full window drops its far end, unless the reader says it is still on screen', async () => {
    const ids = Array.from({ length: WINDOW_CAP + 2 }, (_, i) => `c${i + 1}`);
    api.on('GET', `${BASE}/nodes/part/p1/children`, chapterList(ids));
    for (const id of ids)
      api.on('GET', `${BASE}/nodes/chapter/${id}/children`, paragraphs(`${id}-p`));
    await store.open('dune');
    await expand('p1');
    await store.openFirstChapter();
    for (let i = 1; i <= WINDOW_CAP; i++) await store.loadNext(false);
    expect(store.window()).toEqual(ids.slice(0, WINDOW_CAP + 1));

    await store.loadNext();
    // One chapter in, one out: the window shrinks back toward the cap as the reader moves on.
    expect(store.window()).toEqual(ids.slice(1));
  });

  it('audio mode loads the voice map for loaded chapters', async () => {
    api.on('GET', `${BASE}/nodes/chapter/c1/voices`, {
      'a-i': { voiceName: 'Deep', narratedBy: null },
    });
    await store.open('dune');
    await store.openFirstChapter();
    store.setMode('audio');
    await settle();
    expect(store.itemVoices()['a-i']?.voiceName).toBe('Deep');
  });

  describe('receipts', () => {
    it('expectOwnReceipt settles true on this tab’s receipt, false on timeout, cancel or close', async () => {
      await store.open('dune');

      const own = store.expectOwnReceipt(1000);
      const foreign = store.expectOwnReceipt(1000);
      live.receiptStream.emit(receipt(6, 'NodeTitle', {}, false));
      live.receiptStream.emit(receipt(7, 'NodeTitle', {}, true));
      expect(await own.settled).toBe(true);
      expect(await foreign.settled).toBe(true);

      const late = store.expectOwnReceipt(10);
      expect(await late.settled).toBe(false);

      const cancelled = store.expectOwnReceipt(1000);
      cancelled.cancel();
      expect(await cancelled.settled).toBe(false);

      const closed = store.expectOwnReceipt(1000);
      store.close();
      expect(await closed.settled).toBe(false);
    });

    it('a speaker change on a loaded paragraph reloads that chapter, batched, with no toast', async () => {
      await store.open('dune');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, paragraphs('a', 'b', 'z'));

      live.receiptStream.emit(receipt(6, 'Attribution', { paragraphIds: ['a'] }));
      live.receiptStream.emit(receipt(7, 'Attribution', { paragraphItemIds: ['b-i'] }));
      expect(calls('/nodes/chapter/c1/children')).toBe(1);
      await settle(RECEIPT_BATCH_MS + 20);

      expect(calls('/nodes/chapter/c1/children')).toBe(2);
      expect(store.chapters()[0]?.paragraphs.map((p) => p.id)).toEqual(['a', 'b', 'z']);
      expect(toasts).toEqual([]);
    });

    it('a foreign split reloads the chapter and the tree and toasts once', async () => {
      await store.open('dune');
      await expand('p1');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, paragraphs('a', 'a2'));

      live.receiptStream.emit(receipt(6, 'Structure', { paragraphIds: ['a'] }));
      live.receiptStream.emit(receipt(7, 'Structure', { paragraphIds: ['a'] }));
      expect(toasts).toEqual(['Book updated elsewhere']);
      await settle(RECEIPT_BATCH_MS + 20);

      expect(calls('/book')).toBe(2);
      expect(calls('/nodes/volume/v1/children')).toBe(2);
      expect(calls('/nodes/part/p1/children')).toBe(2);
      expect(store.chapters()[0]?.paragraphs.map((p) => p.id)).toEqual(['a', 'a2']);
    });

    it('an own split does not toast', async () => {
      await store.open('dune');
      await expand('p1');
      await store.openFirstChapter();
      live.receiptStream.emit(receipt(6, 'Structure', { paragraphIds: ['a'] }, true));
      expect(toasts).toEqual([]);
      await settle(RECEIPT_BATCH_MS + 20);
    });

    it('a revision gap reloads overview, reviews and the visible chapter', async () => {
      await store.open('dune');
      await expand('p1');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/audio/reviews`, { 'a-i': { state: 'NeedsReview' } });

      live.receiptStream.emit(receipt(9, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);

      expect(calls('/book')).toBe(2);
      expect(calls('/audio/reviews')).toBe(2);
      expect(calls('/nodes/chapter/c1/children')).toBe(2);
      expect(Object.keys(store.reviews())).toEqual(['a-i']);
    });

    it('a resync ahead of the last revision is treated as a gap', async () => {
      await store.open('dune');
      await expand('p1');
      await store.openFirstChapter();
      live.resyncStream.emit({ projects: { DUNE: { revision: 12 } } } as unknown as LiveSnapshot);
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(2);
      expect(calls('/audio/reviews')).toBe(2);
      expect(calls('/nodes/chapter/c1/children')).toBe(2);
    });

    it('a resync before any baseline adopts its revision and refreshes in place instead of dropping the window', async () => {
      projectStatus.set(null);
      projectRevision.set(0);
      await store.open('dune');
      await store.openFirstChapter();
      live.resyncStream.emit({ projects: { dune: { revision: 3 } } } as unknown as LiveSnapshot);
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(2);
      expect(calls('/audio/reviews')).toBe(2);
      expect(calls('/nodes/chapter/c1/children')).toBe(2);
      expect(store.window()).toEqual(['c1']);
      // The adopted revision is the baseline from now on: the next one is not a gap.
      live.receiptStream.emit(receipt(4, 'Attribution', { paragraphIds: ['a'] }));
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(2);
      expect(calls('/nodes/chapter/c1/children')).toBe(3);
      expect(store.window()).toEqual(['c1']);
    });

    it('an open superseded by another open does not take the current chapter or the scroll', async () => {
      let answer!: (body: unknown) => void;
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, () => new Promise((r) => (answer = r)));
      await store.open('dune');
      await expand('p1');
      const slow = store.openChapter('c1');
      await settle();
      await store.openChapter('c2');
      answer(paragraphs('a'));
      await slow;
      expect(store.window()).toEqual(['c2']);
      expect(store.currentChapterId()).toBe('c2');
      expect(store.scrollRequest()?.chapterId).toBe('c2');
    });

    it('a failed reload marks the view stale; Refresh retries and clears it', async () => {
      await store.open('dune');
      await expand('p1');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, () => problem(500, 'db locked'));

      live.receiptStream.emit(receipt(6, 'ItemText', { paragraphIds: ['a'] }));
      await settle(RECEIPT_BATCH_MS + 20);
      expect(store.stale()).toBeTruthy();

      api.on('GET', `${BASE}/nodes/chapter/c1/children`, paragraphs('a'));
      await store.refresh();
      expect(store.stale()).toBeNull();
    });
  });

  describe('review fixes', () => {
    it('remembers expanded tree nodes and forgets collapsed ones', async () => {
      await store.open('dune');
      await expand('p1');
      expect([...store.expanded()]).toEqual(['p1']);
      store.setExpanded({ ...partNode('p1'), loaded: true }, false);
      expect([...store.expanded()]).toEqual([]);
    });

    it('a failed expand marks the view stale', async () => {
      api.on('GET', `${BASE}/nodes/part/p2/children`, () => problem(500, 'locked'));
      await store.open('dune');
      await expand('p2');
      expect(store.stale()).toBeTruthy();
    });

    it('revision 0 from /status is a real baseline, so revision 2 is a gap', async () => {
      projectRevision.set(0);
      await store.open('dune');
      await expand('p1');
      await store.openFirstChapter();

      live.receiptStream.emit(receipt(2, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(2);
    });

    it('before /status answers there is no baseline, so the first receipt is not a gap', async () => {
      projectStatus.set(null);
      projectRevision.set(0);
      await store.open('dune');
      live.receiptStream.emit(receipt(40, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(1);
    });

    async function windowOfTwo() {
      await store.open('dune');
      await store.openFirstChapter();
      await store.loadNext();
      expect(store.window()).toEqual(['c1', 'c2']);
    }

    async function structuralReload(nodeId: string, chapters: string[]) {
      api.on('GET', `${BASE}/nodes/part/p1/children`, {
        chapters: chapters.map((id) => ({ id, title: id })),
      });
      live.receiptStream.emit(receipt(6, 'Structure', { nodeIds: [nodeId] }));
      await settle(RECEIPT_BATCH_MS + 20);
    }

    it('after a structural reload, a chapter that no longer exists leaves the window', async () => {
      await windowOfTwo();
      await structuralReload('c2', ['c1']);
      expect(store.window()).toEqual(['c1']);
    });

    it('after a structural reload, a chapter split in between the window ends is loaded in', async () => {
      await windowOfTwo();
      api.on('GET', `${BASE}/nodes/chapter/c1b/children`, paragraphs('b2'));
      await structuralReload('c1', ['c1', 'c1b', 'c2']);
      expect(store.window()).toEqual(['c1', 'c1b', 'c2']);
    });

    it('after a structural reload, a chapter in a later part stays open while earlier parts are unread', async () => {
      await store.open('dune');
      await expand('p2');
      await store.openChapter('c3');
      const before = calls('/nodes/part/p1/children');

      live.receiptStream.emit(receipt(6, 'Structure', { nodeIds: ['c3'] }));
      await settle(RECEIPT_BATCH_MS + 20);

      // p1 was never read, so c3 is not in the chapter sequence yet: read on rather than drop it.
      expect(calls('/nodes/part/p1/children')).toBe(before + 1);
      expect(store.window()).toEqual(['c3']);
      expect(store.currentChapterId()).toBe('c3');
    });
  });
});
