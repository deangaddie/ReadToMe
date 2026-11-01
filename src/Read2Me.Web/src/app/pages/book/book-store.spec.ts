import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { BookOverviewDto, ParagraphDto, ProjectDetailDto } from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { LiveSnapshot, Receipt } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { STRUCTURE_RULES } from '../../../testing/structure-rules';
import { FakeApi, problem } from '../../../testing/fake-api';
import type { ProjectStore } from '../project/project-store';
import { BookStore, RECEIPT_BATCH_MS, WINDOW_CAP } from './book-store';
import type { TreeNode } from './book-tree';

const BASE = '/api/projects/dracula';

const DETAIL = {
  narrator: { characterId: 'n', displayName: 'Narrator', isLinked: false },
} as ProjectDetailDto;

const OVERVIEW: BookOverviewDto = {
  hasContent: true,
  volumes: [{ id: 'v1', title: null }],
  characters: [{ id: 'h', name: 'Elizabeth', aliases: [] }],
  totalParts: 2,
  totalChapters: 3,
  revision: 5,
  structureRules: STRUCTURE_RULES,
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
    folder: 'dracula',
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
  /** The project's read-only reason: set while a History step is viewed. */
  let readOnly: ReturnType<typeof signal<string | null>>;

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
    readOnly = signal<string | null>(null);
    const project = { detail: signal(DETAIL), readOnly } as unknown as ProjectStore;
    store = new BookStore(project);

    api
      .on('GET', `${BASE}/book`, OVERVIEW)
      .on('GET', `${BASE}/audio/reviews`, {})
      .on('GET', `${BASE}/nodes/Volume/v1/children`, {
        parts: [
          { id: 'p1', title: 'One' },
          { id: 'p2', title: 'Two' },
        ],
      })
      .on('GET', `${BASE}/nodes/Part/p1/children`, chapterList(['c1', 'c2']))
      .on('GET', `${BASE}/nodes/Part/p2/children`, { chapters: [{ id: 'c3', title: null }] })
      .on('GET', `${BASE}/nodes/Chapter/c1/children`, paragraphs('a', 'b'))
      .on('GET', `${BASE}/nodes/Chapter/c2/children`, paragraphs('c'))
      .on('GET', `${BASE}/nodes/Chapter/c3/children`, paragraphs('d'));
  });

  afterEach(() => store.close());

  function partNode(id: string): TreeNode {
    return {
      id,
      level: 'Part',
      title: id,
      rawTitle: id,
      untitled: false,
      isFirst: true,
      isLast: true,
      expandable: true,
      loaded: false,
      loadTarget: { level: 'Part', id },
      children: [],
    };
  }

  async function expand(part: string) {
    store.setExpanded(partNode(part), true);
    await settle();
  }

  it('opens: overview, reviews, and the single volume collapses to its parts', async () => {
    await store.open('dracula');
    expect(calls('/book')).toBe(1);
    expect(calls('/audio/reviews')).toBe(1);
    expect(store.tree().map((n) => `${n.level}:${n.title}`)).toEqual(['Part:One', 'Part:Two']);
    expect(store.speakers().names).toEqual({ h: 'Elizabeth' });
  });

  it('openFirstChapter while the volume is still loading waits for that read instead of re-asking', async () => {
    let answer!: (parts: unknown) => void;
    const held = new Promise((r) => (answer = r));
    api.on('GET', `${BASE}/nodes/Volume/v1/children`, () => held);
    const opened = store.open('dracula');
    await settle();
    // The volume read is in flight; a concurrent first-chapter request must share it.
    const first = store.openFirstChapter();
    await settle();
    answer({ parts: [{ id: 'p1', title: 'One' }] });
    await opened;
    await first;
    expect(calls('/nodes/Volume/v1/children')).toBe(1);
    expect(store.window()).toEqual(['c1']);
  });

  it('a children read that leaves the node unloaded ends the search instead of looping', async () => {
    await store.open('dracula');
    const first = store.openFirstChapter();
    // Closing mid-read: the store forgets the folder, the read is discarded, and the search ends.
    store.close();
    expect(await first).toBeUndefined();
    expect(store.window()).toEqual([]);
  });

  it('openFirstChapter loads structure down to the first chapter and asks the reader to scroll', async () => {
    await store.open('dracula');
    await store.openFirstChapter();
    expect(store.window()).toEqual(['c1']);
    expect(store.chapters()).toEqual([
      expect.objectContaining({ id: 'c1', title: 'C1', paragraphs: expect.any(Array) }),
    ]);
    expect(store.scrollRequest()).toMatchObject({ chapterId: 'c1' });
  });

  it('loadNext crosses a part boundary, loading the next part on demand', async () => {
    await store.open('dracula');
    await store.openFirstChapter();

    await store.loadNext();
    expect(store.window()).toEqual(['c1', 'c2']);
    expect(calls('/nodes/Part/p2/children')).toBe(0);

    await store.loadNext();
    expect(calls('/nodes/Part/p2/children')).toBe(1);
    expect(store.window()).toEqual(['c1', 'c2', 'c3']);
    expect(store.atEnd()).toBe(true);

    await store.loadPrevious();
    expect(store.window()).toEqual(['c1', 'c2', 'c3']);
  });

  it('atEnd is false while a later part is unread', async () => {
    await store.open('dracula');
    await store.openFirstChapter();
    await store.loadNext();
    // c2 ends p1, but p2 has not been read: there may be more.
    expect(store.atEnd()).toBe(false);
  });

  it('a full window drops its far end, unless the reader says it is still on screen', async () => {
    const ids = Array.from({ length: WINDOW_CAP + 2 }, (_, i) => `c${i + 1}`);
    api.on('GET', `${BASE}/nodes/Part/p1/children`, chapterList(ids));
    for (const id of ids)
      api.on('GET', `${BASE}/nodes/Chapter/${id}/children`, paragraphs(`${id}-p`));
    await store.open('dracula');
    await expand('p1');
    await store.openFirstChapter();
    for (let i = 1; i <= WINDOW_CAP; i++) await store.loadNext(false);
    expect(store.window()).toEqual(ids.slice(0, WINDOW_CAP + 1));

    await store.loadNext();
    // One chapter in, one out: the window shrinks back toward the cap as the reader moves on.
    expect(store.window()).toEqual(ids.slice(1));
  });

  it('paging loadNext through a twelve-chapter book never holds more than five and ends on the last', async () => {
    const ids = Array.from({ length: 12 }, (_, i) => `c${i + 1}`);
    api.on('GET', `${BASE}/nodes/Volume/v1/children`, { parts: [{ id: 'p1', title: 'One' }] });
    api.on('GET', `${BASE}/nodes/Part/p1/children`, chapterList(ids));
    for (const id of ids)
      api.on('GET', `${BASE}/nodes/Chapter/${id}/children`, paragraphs(`${id}-p`));
    await store.open('dracula');
    await store.openFirstChapter();

    let max = store.window().length;
    for (let i = 0; i < ids.length + 2 && !store.atEnd(); i++) {
      await store.loadNext();
      max = Math.max(max, store.window().length);
    }
    expect(max).toBe(WINDOW_CAP);
    expect(store.window()).toEqual(ids.slice(-WINDOW_CAP));
    expect(store.atEnd()).toBe(true);
  });

  it('loadNext crosses a volume boundary, filling the window to five', async () => {
    // Two volumes of two parts of two chapters: v1 → p1 (a, b), p2 (c, d); v2 → p3 (e, f), p4 (g, h).
    api.on('GET', `${BASE}/book`, {
      ...OVERVIEW,
      volumes: [
        { id: 'v1', title: 'Vol 1' },
        { id: 'v2', title: 'Vol 2' },
      ],
      totalParts: 4,
      totalChapters: 8,
    });
    const parts: Record<string, string[]> = { v1: ['p1', 'p2'], v2: ['p3', 'p4'] };
    const chapters: Record<string, string[]> = {
      p1: ['a', 'b'],
      p2: ['c', 'd'],
      p3: ['e', 'f'],
      p4: ['g', 'h'],
    };
    for (const [volume, ids] of Object.entries(parts))
      api.on('GET', `${BASE}/nodes/Volume/${volume}/children`, {
        parts: ids.map((id) => ({ id, title: id })),
      });
    for (const [part, ids] of Object.entries(chapters)) {
      api.on('GET', `${BASE}/nodes/Part/${part}/children`, chapterList(ids));
      for (const id of ids)
        api.on('GET', `${BASE}/nodes/Chapter/${id}/children`, paragraphs(`${id}-p`));
    }
    await store.open('dracula');
    await store.openFirstChapter();
    expect(calls('/nodes/Volume/v2/children')).toBe(0);

    for (let i = 1; i < WINDOW_CAP; i++) await store.loadNext();
    expect(calls('/nodes/Volume/v2/children')).toBe(1);
    expect(store.window()).toEqual(['a', 'b', 'c', 'd', 'e']);
    expect(store.chapters().map((c) => c.id)).toEqual(['a', 'b', 'c', 'd', 'e']);
  });

  it('audio mode loads the voice map for loaded chapters', async () => {
    api.on('GET', `${BASE}/nodes/Chapter/c1/voices`, {
      'a-i': { voiceName: 'Deep', narratedBy: null },
    });
    await store.open('dracula');
    await store.openFirstChapter();
    store.setMode('audio');
    await settle();
    expect(store.itemVoices()['a-i']?.voiceName).toBe('Deep');
  });

  describe('receipts', () => {
    it('expectOwnReceipt settles true on this tab’s receipt, false on timeout, cancel or close', async () => {
      await store.open('dracula');

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
      await store.open('dracula');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/nodes/Chapter/c1/children`, paragraphs('a', 'b', 'z'));

      live.receiptStream.emit(receipt(6, 'Attribution', { paragraphIds: ['a'] }));
      live.receiptStream.emit(receipt(7, 'Attribution', { paragraphItemIds: ['b-i'] }));
      expect(calls('/nodes/Chapter/c1/children')).toBe(1);
      await settle(RECEIPT_BATCH_MS + 20);

      expect(calls('/nodes/Chapter/c1/children')).toBe(2);
      expect(store.chapters()[0]?.paragraphs.map((p) => p.id)).toEqual(['a', 'b', 'z']);
      expect(toasts).toEqual([]);
    });

    it('a foreign split reloads the chapter and the tree and toasts once', async () => {
      await store.open('dracula');
      await expand('p1');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/nodes/Chapter/c1/children`, paragraphs('a', 'a2'));

      live.receiptStream.emit(receipt(6, 'Structure', { paragraphIds: ['a'] }));
      live.receiptStream.emit(receipt(7, 'Structure', { paragraphIds: ['a'] }));
      expect(toasts).toEqual(['Book updated elsewhere']);
      await settle(RECEIPT_BATCH_MS + 20);

      expect(calls('/book')).toBe(2);
      expect(calls('/nodes/Volume/v1/children')).toBe(2);
      expect(calls('/nodes/Part/p1/children')).toBe(2);
      expect(store.chapters()[0]?.paragraphs.map((p) => p.id)).toEqual(['a', 'a2']);
    });

    it('while viewing a History step, a foreign split rereads (at the step) without the toast', async () => {
      await store.open('dracula');
      await expand('p1');
      await store.openFirstChapter();
      readOnly.set('Viewing a past step');

      live.receiptStream.emit(receipt(6, 'Structure', { paragraphIds: ['a'] }));
      await settle(RECEIPT_BATCH_MS + 20);

      expect(toasts).toEqual([]);
      expect(calls('/nodes/Chapter/c1/children')).toBe(2);
    });

    it('an own split does not toast', async () => {
      await store.open('dracula');
      await expand('p1');
      await store.openFirstChapter();
      live.receiptStream.emit(receipt(6, 'Structure', { paragraphIds: ['a'] }, true));
      expect(toasts).toEqual([]);
      await settle(RECEIPT_BATCH_MS + 20);
    });

    it('a revision gap reloads overview, reviews and the visible chapter', async () => {
      await store.open('dracula');
      await expand('p1');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/audio/reviews`, { 'a-i': { state: 'NeedsReview' } });

      live.receiptStream.emit(receipt(9, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);

      expect(calls('/book')).toBe(2);
      expect(calls('/audio/reviews')).toBe(2);
      expect(calls('/nodes/Chapter/c1/children')).toBe(2);
      expect(Object.keys(store.reviews())).toEqual(['a-i']);
    });

    it('a resync ahead of the last revision is treated as a gap', async () => {
      await store.open('dracula');
      await expand('p1');
      await store.openFirstChapter();
      live.resyncStream.emit({
        projects: { DRACULA: { revision: 12 } },
      } as unknown as LiveSnapshot);
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(2);
      expect(calls('/audio/reviews')).toBe(2);
      expect(calls('/nodes/Chapter/c1/children')).toBe(2);
    });

    it('a resync at the revision /book read against reads nothing again', async () => {
      await store.open('dracula');
      await store.openFirstChapter();
      live.resyncStream.emit({ projects: { dracula: { revision: 5 } } } as unknown as LiveSnapshot);
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(1);
      expect(calls('/audio/reviews')).toBe(1);
      expect(calls('/nodes/Chapter/c1/children')).toBe(1);
    });

    describe('a resync while /book is in flight', () => {
      let answer!: (body: unknown) => void;
      let opened: Promise<void>;

      beforeEach(async () => {
        api.on('GET', `${BASE}/book`, () => new Promise((r) => (answer = r)));
        opened = store.open('dracula');
        await settle();
      });

      async function snapshotThenBook(snapshot: number, book: number) {
        live.resyncStream.emit({
          projects: { dracula: { revision: snapshot } },
        } as unknown as LiveSnapshot);
        answer({ ...OVERVIEW, revision: book });
        await opened;
        api.on('GET', `${BASE}/book`, OVERVIEW);
        await store.openFirstChapter();
        await settle(RECEIPT_BATCH_MS + 20);
      }

      it('waits for the baseline and reads nothing again when the book already holds that revision', async () => {
        await snapshotThenBook(5, 5);
        expect(calls('/book')).toBe(1);
        expect(calls('/audio/reviews')).toBe(1);
        expect(calls('/nodes/Chapter/c1/children')).toBe(1);
      });

      it('reloads when the snapshot is ahead of the book, even if a later receipt came meanwhile', async () => {
        // Commit 6 landed before the hub joined (no receipt); the receipt for 7 is not a gap yet.
        live.receiptStream.emit(receipt(7, 'VoiceRules'));
        await snapshotThenBook(6, 5);
        expect(calls('/book')).toBe(2);
        expect(calls('/audio/reviews')).toBe(2);
      });

      it('reloads in place, keeping the window, when the snapshot is ahead of the book', async () => {
        await snapshotThenBook(6, 5);
        expect(calls('/book')).toBe(2);
        expect(calls('/audio/reviews')).toBe(2);
        expect(store.window()).toEqual(['c1']);
        expect(store.currentChapterId()).toBe('c1');
        // The snapshot's revision is the baseline from now on: the next one is not a gap.
        live.receiptStream.emit(receipt(7, 'Attribution', { paragraphIds: ['a'] }));
        await settle(RECEIPT_BATCH_MS + 20);
        expect(calls('/book')).toBe(2);
      });
    });

    it('a failed opening read takes its baseline from Refresh, so a later snapshot ahead still reloads', async () => {
      api.on('GET', `${BASE}/book`, () => problem(500, 'db locked'));
      await store.open('dracula');
      expect(store.stale()).toBeTruthy();
      live.resyncStream.emit({ projects: { dracula: { revision: 5 } } } as unknown as LiveSnapshot);

      api.on('GET', `${BASE}/book`, OVERVIEW);
      await store.refresh();
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(2);

      live.resyncStream.emit({ projects: { dracula: { revision: 6 } } } as unknown as LiveSnapshot);
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(3);
    });

    it('an open superseded by another open does not take the current chapter or the scroll', async () => {
      let answer!: (body: unknown) => void;
      api.on('GET', `${BASE}/nodes/Chapter/c1/children`, () => new Promise((r) => (answer = r)));
      await store.open('dracula');
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
      await store.open('dracula');
      await expand('p1');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/nodes/Chapter/c1/children`, () => problem(500, 'db locked'));

      live.receiptStream.emit(receipt(6, 'ItemText', { paragraphIds: ['a'] }));
      await settle(RECEIPT_BATCH_MS + 20);
      expect(store.stale()).toBeTruthy();

      api.on('GET', `${BASE}/nodes/Chapter/c1/children`, paragraphs('a'));
      await store.refresh();
      expect(store.stale()).toBeNull();
    });
  });

  describe('review fixes', () => {
    it('remembers expanded tree nodes and forgets collapsed ones', async () => {
      await store.open('dracula');
      await expand('p1');
      expect([...store.expanded()]).toEqual(['p1']);
      store.setExpanded({ ...partNode('p1'), loaded: true }, false);
      expect([...store.expanded()]).toEqual([]);
    });

    it('a failed expand marks the view stale', async () => {
      api.on('GET', `${BASE}/nodes/Part/p2/children`, () => problem(500, 'locked'));
      await store.open('dracula');
      await expand('p2');
      expect(store.stale()).toBeTruthy();
    });

    it('revision 0 from /book is a real baseline, so revision 2 is a gap', async () => {
      api.on('GET', `${BASE}/book`, { ...OVERVIEW, revision: 0 });
      await store.open('dracula');
      await expand('p1');
      await store.openFirstChapter();

      live.receiptStream.emit(receipt(2, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(2);
    });

    it('before /book answers there is no baseline, so the first receipt is not a gap', async () => {
      let answer!: (body: unknown) => void;
      api.on('GET', `${BASE}/book`, () => new Promise((r) => (answer = r)));
      const opened = store.open('dracula');
      await settle();
      live.receiptStream.emit(receipt(40, 'VoiceRules'));
      answer(OVERVIEW);
      await opened;
      await settle(RECEIPT_BATCH_MS + 20);
      expect(calls('/book')).toBe(1);
    });

    async function windowOfTwo() {
      await store.open('dracula');
      await store.openFirstChapter();
      await store.loadNext();
      expect(store.window()).toEqual(['c1', 'c2']);
    }

    async function structuralReload(nodeId: string, chapters: string[]) {
      api.on('GET', `${BASE}/nodes/Part/p1/children`, {
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
      api.on('GET', `${BASE}/nodes/Chapter/c1b/children`, paragraphs('b2'));
      await structuralReload('c1', ['c1', 'c1b', 'c2']);
      expect(store.window()).toEqual(['c1', 'c1b', 'c2']);
    });

    it('after a structural reload, a chapter in a later part stays open while earlier parts are unread', async () => {
      await store.open('dracula');
      await expand('p2');
      await store.openChapter('c3');
      const before = calls('/nodes/Part/p1/children');

      live.receiptStream.emit(receipt(6, 'Structure', { nodeIds: ['c3'] }));
      await settle(RECEIPT_BATCH_MS + 20);

      // p1 was never read, so c3 is not in the chapter sequence yet: read on rather than drop it.
      expect(calls('/nodes/Part/p1/children')).toBe(before + 1);
      expect(store.window()).toEqual(['c3']);
      expect(store.currentChapterId()).toBe('c3');
    });
  });

  describe('revealing a paragraph this view created (own receipt)', () => {
    /** The receipt a Node insertion commits for a new paragraph `id` in `chapter`. */
    const created = (id: string, chapter: string, isOwn = true) =>
      receipt(
        6,
        'Structure',
        {
          createdId: id,
          createdParentId: chapter,
          paragraphIds: [id],
          paragraphItemIds: [`${id}-i`],
        },
        isOwn,
      );

    it('a chapter outside the loaded window is opened and scrolled to', async () => {
      await store.open('dracula');
      await store.openFirstChapter();
      api.on('GET', `${BASE}/nodes/Chapter/c3/children`, paragraphs('new'));

      live.receiptStream.emit(created('new', 'c3'));
      await settle(RECEIPT_BATCH_MS + 20);

      expect(store.window()).toEqual(['c3']);
      expect(store.scrollRequest()).toMatchObject({ chapterId: 'c3' });
      expect(store.chapters()[0]?.paragraphs.map((p) => p.id)).toEqual(['new']);
    });

    it('a loaded chapter off the visible one is read again in place, without scrolling', async () => {
      await store.open('dracula');
      await store.openFirstChapter();
      await store.loadNext();
      const scroll = store.scrollRequest();
      api.on('GET', `${BASE}/nodes/Chapter/c2/children`, paragraphs('c', 'new'));

      live.receiptStream.emit(created('new', 'c2'));
      await settle(RECEIPT_BATCH_MS + 20);

      expect(store.window()).toEqual(['c1', 'c2']);
      expect(store.scrollRequest()).toBe(scroll);
      expect(store.chapters()[1]?.paragraphs.map((p) => p.id)).toEqual(['c', 'new']);
    });

    it("another view's receipt does not reveal it", async () => {
      await store.open('dracula');
      await store.openFirstChapter();
      const before = calls('/nodes/Chapter/c3/children');

      live.receiptStream.emit(created('new', 'c3', false));
      await settle(RECEIPT_BATCH_MS + 20);

      expect(store.window()).toEqual(['c1']);
      expect(calls('/nodes/Chapter/c3/children')).toBe(before);
    });

    it('a created item is reread in place, not revealed', async () => {
      await store.open('dracula');
      await store.openFirstChapter();
      const scroll = store.scrollRequest();
      const before = calls('/nodes/Chapter/c1/children');

      live.receiptStream.emit(
        receipt(
          6,
          'Structure',
          {
            createdId: 'a-new',
            createdParentId: 'a',
            paragraphIds: ['a'],
            paragraphItemIds: ['a-new'],
          },
          true,
        ),
      );
      await settle(RECEIPT_BATCH_MS + 20);

      expect(store.window()).toEqual(['c1']);
      expect(store.scrollRequest()).toBe(scroll);
      expect(calls('/nodes/Chapter/c1/children')).toBe(before + 1);
    });
  });
});
