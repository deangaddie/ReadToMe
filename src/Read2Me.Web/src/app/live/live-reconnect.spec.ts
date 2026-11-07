import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { BookOverviewDto, ProjectDetailDto, ReceiptMessage } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { ToastService } from '@app/ui/toast';
import { BookStore, RECEIPT_BATCH_MS } from '@app/pages/book/book-store';
import type { ProjectStore } from '@app/pages/project/project-store';
import { STRUCTURE_RULES } from '../../testing/structure-rules';
import { FakeApi } from '../../testing/fake-api';
import { FakeConnection, hubSnapshot, projectSnapshot } from '../../testing/fake-hub-connection';
import { LIVE_CONNECTION_FACTORY, type LiveConnectionOptions } from './live-connection';
import { LiveService } from './live.service';

/**
 * An open Book view across a lost hub connection (the logic behind `LiveUpdateTests`): the real
 * `LiveService` over a fake socket, feeding the real `BookStore` over `FakeApi`. After the drop the
 * service re-joins the project's group before it asks for the snapshot, a receipt on the healed
 * connection reaches the open book, and a split made while the hub was away is read from the
 * snapshot alone. The bundle's SignalR client against the real hub is `LiveUpdateTests`' one case.
 */

const FOLDER = 'dracula';
const BASE = `/api/projects/${FOLDER}`;

const OVERVIEW: BookOverviewDto = {
  hasContent: true,
  volumes: [{ id: 'v1', title: null }],
  characters: [],
  totalParts: 1,
  totalChapters: 1,
  revision: 5,
  structureRules: STRUCTURE_RULES,
};

const paragraphs = (...ids: string[]) => ({
  paragraphs: ids.map((id) => ({ id, isPauseParagraph: false, items: [] })),
});

/** A split made in another browser, as the hub sends its receipt to the project's group. */
function splitReceipt(revision: number): ReceiptMessage {
  return {
    folder: FOLDER,
    mutationName: 'SplitChapterMutation',
    mutationId: `m${revision}`,
    revision,
    originId: 'another-browser',
    effects: {
      scope: 'Exact',
      createdId: 'c2',
      createdParentId: 'p1',
      facets: ['Structure'],
      nodeIds: ['c1'],
      paragraphIds: ['b'],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
    },
  };
}

describe('an open book across a lost hub connection', () => {
  let api: FakeApi;
  let connections: FakeConnection[];
  let toasts: string[];
  let live: LiveService;
  let book: BookStore;

  const connection = () => connections.at(-1)!;
  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));
  const reads = (path: string) => api.calls('GET', `${BASE}${path}`).length;
  const chapterIds = () => book.chapters().map((c) => c.id);
  /** The hub methods invoked since `from`, in order. */
  const invokedSince = (from: number) =>
    connection()
      .invocations.slice(from)
      .map((i) => i.method);

  beforeEach(async () => {
    resetServices();
    api = new FakeApi();
    api.install();
    connections = [];
    toasts = [];
    override(LIVE_CONNECTION_FACTORY, (options: LiveConnectionOptions) => {
      const c = new FakeConnection(options);
      c.results.set('GetSnapshot', () => hubSnapshot());
      c.results.set('JoinProject', (folder) => projectSnapshot(String(folder), 5));
      connections.push(c);
      return c;
    });
    const toast = (m: string) => toasts.push(m);
    override(ToastService, { info: toast, success: toast, warn: toast } as unknown as ToastService);
    api
      .on('GET', `${BASE}/book`, OVERVIEW)
      .on('GET', `${BASE}/audio/reviews`, {})
      .on('GET', `${BASE}/nodes/Volume/v1/children`, { parts: [{ id: 'p1', title: null }] })
      .on('GET', `${BASE}/nodes/Part/p1/children`, { chapters: [{ id: 'c1', title: 'ch1' }] })
      .on('GET', `${BASE}/nodes/Chapter/c1/children`, paragraphs('a', 'b'));

    // The app starts the service; the project shell joins the group and opens the book.
    live = new LiveService();
    override(LiveService, live);
    live.start();
    await live.joinProject(FOLDER);
    const project = { detail: signal({} as ProjectDetailDto), readOnly: signal(null) };
    book = new BookStore(project as unknown as ProjectStore);
    await book.open(FOLDER);
    await book.openFirstChapter();
    await settle();
    expect(live.state()).toBe('connected');
    expect(chapterIds()).toEqual(['c1']);
  });

  afterEach(async () => {
    book.close();
    await live.stop();
  });

  /** What the other browser's split leaves on the host: c1 ends at a, c2 starts at b. */
  function splitOnTheHost() {
    api
      .on('GET', `${BASE}/book`, { ...OVERVIEW, totalChapters: 2, revision: 6 })
      .on('GET', `${BASE}/nodes/Part/p1/children`, {
        chapters: [
          { id: 'c1', title: 'ch1' },
          { id: 'c2', title: 'From elsewhere' },
        ],
      })
      .on('GET', `${BASE}/nodes/Chapter/c1/children`, paragraphs('a'))
      .on('GET', `${BASE}/nodes/Chapter/c2/children`, paragraphs('b'));
  }

  it('re-joins the group before the snapshot, and a split elsewhere then reaches the book by its receipt', async () => {
    const before = connection().invocations.length;
    connection().drop();
    expect(live.state()).toBe('reconnecting');
    connection().recover();
    await settle();

    expect(live.state()).toBe('connected');
    // The snapshot is read once the group is joined again, so it covers every commit since.
    expect(invokedSince(before)).toEqual(['JoinProject', 'GetSnapshot']);
    expect(connection().calls('JoinProject').at(-1)).toEqual([FOLDER]);
    expect(reads('/book')).toBe(1); // the snapshot is at the book's revision: nothing to read

    splitOnTheHost();
    connection().emit('receipt', splitReceipt(6));
    expect(toasts).toEqual(['Book updated elsewhere']);
    await settle(RECEIPT_BATCH_MS + 20);

    expect(reads('/book')).toBe(2);
    expect(reads('/nodes/Part/p1/children')).toBe(2);
    expect(book.sequence().map((c) => c.title)).toEqual(['ch1', 'From elsewhere']);
  });

  it('a split made while the hub was gone is read from the snapshot ahead of the book, with no receipt', async () => {
    splitOnTheHost();
    connection().results.set('GetSnapshot', () =>
      hubSnapshot({ [FOLDER]: projectSnapshot(FOLDER, 6) }),
    );
    const before = connection().invocations.length;

    // Lost for good: the service starts the connection again itself.
    connection().close();
    expect(live.state()).toBe('disconnected');
    await settle(RECEIPT_BATCH_MS + 20);

    expect(live.state()).toBe('connected');
    expect(invokedSince(before)).toEqual(['JoinProject', 'GetSnapshot']);
    expect(reads('/book')).toBe(2);
    expect(reads('/nodes/Chapter/c1/children')).toBe(2);
    expect(book.sequence().map((c) => c.title)).toEqual(['ch1', 'From elsewhere']);
  });

  it('a receipt for another project on the healed connection reads nothing', async () => {
    connection().drop();
    connection().recover();
    await settle();

    connection().emit('receipt', { ...splitReceipt(6), folder: 'carmilla' });
    await settle(RECEIPT_BATCH_MS + 20);

    expect(toasts).toEqual([]);
    expect(reads('/book')).toBe(1);
  });
});
