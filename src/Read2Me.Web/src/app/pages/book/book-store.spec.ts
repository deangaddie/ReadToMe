import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BookOverviewDto, ParagraphDto, ProjectDetailDto } from '@app/api';
import { LiveSnapshot, Receipt } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { Subject } from 'rxjs';
import { ProjectStore } from '../project/project-store';
import { BookStore, RECEIPT_BATCH_MS } from './book-store';
import { TreeNode } from './book-tree';

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

class FakeLive {
  readonly receipts = new Subject<Receipt>();
  readonly resynced = new Subject<LiveSnapshot>();
  readonly resynced$ = this.resynced.asObservable();
  receipts$() {
    return this.receipts.asObservable();
  }
}

describe('BookStore', () => {
  let http: HttpTestingController;
  let live: FakeLive;
  let store: BookStore;
  let toasts: string[];
  let projectStatus: ReturnType<typeof signal<object | null>>;
  let projectRevision: ReturnType<typeof signal<number>>;

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  beforeEach(() => {
    live = new FakeLive();
    toasts = [];
    projectStatus = signal<object | null>({});
    projectRevision = signal(5);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        BookStore,
        { provide: LiveService, useValue: live },
        {
          provide: ProjectStore,
          useValue: { revision: projectRevision, status: projectStatus, detail: signal(DETAIL) },
        },
        { provide: ToastService, useValue: { info: (m: string) => toasts.push(m) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(BookStore);
  });

  afterEach(() => {
    store.close();
    http.verify();
  });

  /** One volume, two parts: p1 → c1, c2; p2 → c3. */
  async function open() {
    const opened = store.open('dune');
    http.expectOne(`${BASE}/book`).flush(OVERVIEW);
    http.expectOne(`${BASE}/audio/reviews`).flush({});
    await settle();
    http.expectOne(`${BASE}/nodes/volume/v1/children`).flush({
      parts: [
        { id: 'p1', title: 'One' },
        { id: 'p2', title: 'Two' },
      ],
    });
    await opened;
  }

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

  async function expand(part: string, chapters: string[]) {
    store.setExpanded(partNode(part), true);
    http
      .expectOne(`${BASE}/nodes/part/${part}/children`)
      .flush({ chapters: chapters.map((id) => ({ id, title: id.toUpperCase() })) });
    await settle();
  }

  async function openFirst() {
    const opening = store.openFirstChapter();
    await settle();
    http.expectOne(`${BASE}/nodes/part/p1/children`).flush({
      chapters: [
        { id: 'c1', title: 'C1' },
        { id: 'c2', title: 'C2' },
      ],
    });
    await settle();
    http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a', 'b'));
    await opening;
  }

  it('opens: overview, reviews, and the single volume collapses to its parts', async () => {
    await open();
    expect(store.tree().map((n) => `${n.level}:${n.title}`)).toEqual(['part:One', 'part:Two']);
    expect(store.speakers().names).toEqual({ h: 'Hardin' });
  });

  it('openFirstChapter while the volume is still loading waits for that read instead of re-asking', async () => {
    const opened = store.open('dune');
    http.expectOne(`${BASE}/book`).flush(OVERVIEW);
    http.expectOne(`${BASE}/audio/reviews`).flush({});
    await settle();
    // The volume read is in flight; a concurrent first-chapter request must share it.
    const first = store.openFirstChapter();
    await settle();
    http.expectOne(`${BASE}/nodes/volume/v1/children`).flush({
      parts: [
        { id: 'p1', title: 'One' },
        { id: 'p2', title: 'Two' },
      ],
    });
    await opened;
    await settle();
    http.expectOne(`${BASE}/nodes/part/p1/children`).flush({ chapters: [{ id: 'c1', title: 'C1' }] });
    await settle();
    http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a'));
    await first;
    expect(store.window()).toEqual(['c1']);
  });

  it('a children read that leaves the node unloaded ends the search instead of looping', async () => {
    await open();
    const first = store.openFirstChapter();
    await settle();
    // Closing mid-read: the store forgets the folder, the read is discarded, and the search ends.
    store.close();
    http.expectOne(`${BASE}/nodes/part/p1/children`).flush({ chapters: [{ id: 'c1', title: 'C1' }] });
    await expect(first).resolves.toBeUndefined();
    expect(store.window()).toEqual([]);
  });

  it('openFirstChapter loads structure down to the first chapter and asks the reader to scroll', async () => {
    await open();
    await openFirst();
    expect(store.window()).toEqual(['c1']);
    expect(store.chapters()).toEqual([
      expect.objectContaining({ id: 'c1', title: 'C1', paragraphs: expect.any(Array) }),
    ]);
    expect(store.scrollRequest()).toMatchObject({ chapterId: 'c1' });
  });

  it('loadNext crosses a part boundary, loading the next part on demand', async () => {
    await open();
    await openFirst();

    const next = store.loadNext();
    await settle();
    http.expectOne(`${BASE}/nodes/chapter/c2/children`).flush(paragraphs('c'));
    await next;

    const past = store.loadNext();
    await settle();
    http
      .expectOne(`${BASE}/nodes/part/p2/children`)
      .flush({ chapters: [{ id: 'c3', title: null }] });
    await settle();
    http.expectOne(`${BASE}/nodes/chapter/c3/children`).flush(paragraphs('d'));
    await past;

    expect(store.window()).toEqual(['c1', 'c2', 'c3']);
    const previous = store.loadPrevious();
    await previous;
    expect(store.window()).toEqual(['c1', 'c2', 'c3']);
  });

  it('audio mode loads the voice map for loaded chapters', async () => {
    await open();
    await openFirst();
    store.setMode('audio');
    http
      .expectOne(`${BASE}/nodes/chapter/c1/voices`)
      .flush({ 'a-i': { voiceName: 'Deep', narratedBy: null } });
    await settle();
    expect(store.itemVoices()['a-i']?.voiceName).toBe('Deep');
  });

  describe('receipts', () => {
    it('expectOwnReceipt settles true on this tab’s receipt, false on timeout, cancel or close', async () => {
      await open();

      const own = store.expectOwnReceipt(1000);
      const foreign = store.expectOwnReceipt(1000);
      live.receipts.next(receipt(6, 'NodeTitle', {}, false));
      live.receipts.next(receipt(7, 'NodeTitle', {}, true));
      await expect(own.settled).resolves.toBe(true);
      await expect(foreign.settled).resolves.toBe(true);

      const late = store.expectOwnReceipt(10);
      await expect(late.settled).resolves.toBe(false);

      const cancelled = store.expectOwnReceipt(1000);
      cancelled.cancel();
      await expect(cancelled.settled).resolves.toBe(false);

      const closed = store.expectOwnReceipt(1000);
      store.close();
      await expect(closed.settled).resolves.toBe(false);
      await settle(RECEIPT_BATCH_MS + 20);
      http.match(() => true).forEach((r) => r.flush({}));
    });

    it('a speaker change on a loaded paragraph reloads that chapter, batched, with no toast', async () => {
      await open();
      await openFirst();

      live.receipts.next(receipt(6, 'Attribution', { paragraphIds: ['a'] }));
      live.receipts.next(receipt(7, 'Attribution', { paragraphItemIds: ['b-i'] }));
      http.expectNone(`${BASE}/nodes/chapter/c1/children`);
      await settle(RECEIPT_BATCH_MS + 20);

      http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a', 'b', 'z'));
      await settle();
      expect(store.chapters()[0]?.paragraphs.map((p) => p.id)).toEqual(['a', 'b', 'z']);
      expect(toasts).toEqual([]);
    });

    it('a foreign split reloads the chapter and the tree and toasts once', async () => {
      await open();
      await expand('p1', ['c1']);
      await openFirstFromLoaded();

      live.receipts.next(receipt(6, 'Structure', { paragraphIds: ['a'] }));
      live.receipts.next(receipt(7, 'Structure', { paragraphIds: ['a'] }));
      expect(toasts).toEqual(['Book updated elsewhere']);
      await settle(RECEIPT_BATCH_MS + 20);

      http.expectOne(`${BASE}/book`).flush(OVERVIEW);
      http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a', 'a2'));
      await settle();
      http
        .expectOne(`${BASE}/nodes/volume/v1/children`)
        .flush({ parts: [{ id: 'p1' }, { id: 'p2' }] });
      http
        .expectOne(`${BASE}/nodes/part/p1/children`)
        .flush({ chapters: [{ id: 'c1', title: 'C1' }] });
      await settle();
      expect(store.chapters()[0]?.paragraphs.map((p) => p.id)).toEqual(['a', 'a2']);
    });

    it('an own split does not toast', async () => {
      await open();
      await expand('p1', ['c1']);
      await openFirstFromLoaded();
      live.receipts.next(receipt(6, 'Structure', { paragraphIds: ['a'] }, true));
      expect(toasts).toEqual([]);
      await settle(RECEIPT_BATCH_MS + 20);
      http
        .match(() => true)
        .forEach((r) => r.flush(r.request.url.endsWith('/book') ? OVERVIEW : {}));
      await settle();
      http.match(() => true).forEach((r) => r.flush({}));
    });

    it('a revision gap reloads overview, reviews and the visible chapter', async () => {
      await open();
      await expand('p1', ['c1']);
      await openFirstFromLoaded();

      live.receipts.next(receipt(9, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);

      http.expectOne(`${BASE}/book`).flush(OVERVIEW);
      http.expectOne(`${BASE}/audio/reviews`).flush({ 'a-i': { state: 'NeedsReview' } });
      http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a'));
      await settle();
      http
        .match(`${BASE}/nodes/volume/v1/children`)
        .forEach((r) => r.flush({ parts: [{ id: 'p1' }] }));
      http
        .match(`${BASE}/nodes/part/p1/children`)
        .forEach((r) => r.flush({ chapters: [{ id: 'c1' }] }));
      await settle();
      expect(Object.keys(store.reviews())).toEqual(['a-i']);
    });

    it('a resync ahead of the last revision is treated as a gap', async () => {
      await open();
      await expand('p1', ['c1']);
      await openFirstFromLoaded();
      live.resynced.next({ projects: { DUNE: { revision: 12 } } } as unknown as LiveSnapshot);
      await settle(RECEIPT_BATCH_MS + 20);
      http.expectOne(`${BASE}/book`).flush(OVERVIEW);
      http.expectOne(`${BASE}/audio/reviews`).flush({});
      http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a'));
      await settle();
      http.match(() => true).forEach((r) => r.flush({ parts: [], chapters: [] }));
      await settle();
    });

    it('a failed reload marks the view stale; Refresh retries and clears it', async () => {
      await open();
      await expand('p1', ['c1']);
      await openFirstFromLoaded();

      live.receipts.next(receipt(6, 'ItemText', { paragraphIds: ['a'] }));
      await settle(RECEIPT_BATCH_MS + 20);
      http
        .expectOne(`${BASE}/nodes/chapter/c1/children`)
        .flush({ detail: 'db locked' }, { status: 500, statusText: 'Error' });
      await settle();
      expect(store.stale()).toBeTruthy();

      const refreshed = store.refresh();
      http.expectOne(`${BASE}/book`).flush(OVERVIEW);
      http.expectOne(`${BASE}/audio/reviews`).flush({});
      http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a'));
      await settle();
      http
        .match(`${BASE}/nodes/volume/v1/children`)
        .forEach((r) => r.flush({ parts: [{ id: 'p1' }] }));
      http
        .match(`${BASE}/nodes/part/p1/children`)
        .forEach((r) => r.flush({ chapters: [{ id: 'c1' }] }));
      await refreshed;
      expect(store.stale()).toBeNull();
    });
  });

  describe('review fixes', () => {
    it('remembers expanded tree nodes and forgets collapsed ones', async () => {
      await open();
      await expand('p1', ['c1']);
      expect([...store.expanded()]).toEqual(['p1']);
      store.setExpanded({ ...partNode('p1'), loaded: true }, false);
      expect([...store.expanded()]).toEqual([]);
    });

    it('a failed expand marks the view stale', async () => {
      await open();
      store.setExpanded(partNode('p2'), true);
      http
        .expectOne(`${BASE}/nodes/part/p2/children`)
        .flush({ detail: 'locked' }, { status: 500, statusText: 'Error' });
      await settle();
      expect(store.stale()).toBeTruthy();
    });

    it('revision 0 from /status is a real baseline, so revision 2 is a gap', async () => {
      projectRevision.set(0);
      await open();
      await expand('p1', ['c1']);
      await openFirstFromLoaded();

      live.receipts.next(receipt(2, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);
      http.expectOne(`${BASE}/book`).flush(OVERVIEW);
      http.expectOne(`${BASE}/audio/reviews`).flush({});
      http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a'));
      await settle();
      http
        .match(`${BASE}/nodes/volume/v1/children`)
        .forEach((r) => r.flush({ parts: [{ id: 'p1' }] }));
      http
        .match(`${BASE}/nodes/part/p1/children`)
        .forEach((r) => r.flush({ chapters: [{ id: 'c1' }] }));
      await settle();
    });

    it('before /status answers there is no baseline, so the first receipt is not a gap', async () => {
      projectStatus.set(null);
      projectRevision.set(0);
      await open();
      live.receipts.next(receipt(40, 'VoiceRules'));
      await settle(RECEIPT_BATCH_MS + 20);
      http.expectNone(`${BASE}/book`);
    });

    async function windowOfTwo() {
      await open();
      await openFirst();
      const next = store.loadNext();
      await settle();
      http.expectOne(`${BASE}/nodes/chapter/c2/children`).flush(paragraphs('c'));
      await next;
      expect(store.window()).toEqual(['c1', 'c2']);
    }

    async function structuralReload(nodeId: string, chapters: string[]) {
      live.receipts.next(receipt(6, 'Structure', { nodeIds: [nodeId] }));
      await settle(RECEIPT_BATCH_MS + 20);
      http.expectOne(`${BASE}/book`).flush(OVERVIEW);
      http.expectOne(`${BASE}/nodes/chapter/${nodeId}/children`).flush(paragraphs('x'));
      await settle();
      http.expectOne(`${BASE}/nodes/volume/v1/children`).flush({ parts: [{ id: 'p1' }] });
      http
        .expectOne(`${BASE}/nodes/part/p1/children`)
        .flush({ chapters: chapters.map((id) => ({ id, title: id })) });
      await settle();
    }

    it('after a structural reload, a chapter that no longer exists leaves the window', async () => {
      await windowOfTwo();
      await structuralReload('c2', ['c1']);
      expect(store.window()).toEqual(['c1']);
    });

    it('after a structural reload, a chapter split in between the window ends is loaded in', async () => {
      await windowOfTwo();
      await structuralReload('c1', ['c1', 'c1b', 'c2']);
      http.expectOne(`${BASE}/nodes/chapter/c1b/children`).flush(paragraphs('b2'));
      await settle();
      expect(store.window()).toEqual(['c1', 'c1b', 'c2']);
    });
  });

  /** After `expand('p1', …)`, the first chapter needs only its paragraphs. */
  async function openFirstFromLoaded() {
    const opening = store.openFirstChapter();
    await settle();
    http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush(paragraphs('a', 'b'));
    await opening;
  }
});
