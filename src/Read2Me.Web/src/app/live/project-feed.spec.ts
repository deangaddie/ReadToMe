import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { BookFacets, ProjectSnapshot, ReceiptMessage } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { ToastService } from '@app/ui/toast';
import { FakeConnection, hubSnapshot, projectSnapshot } from '../../testing/fake-hub-connection';
import { FakeTimers, settleMicrotasks as settle } from '../../testing/fake-timers';
import { LIVE_CONNECTION_FACTORY, type LiveConnectionOptions } from './live-connection';
import { LiveService } from './live.service';
import { type FollowHandle, ProjectFeed } from './project-feed';

/**
 * The Project feed over the real `LiveService` and a fake socket: what one follower hears of one
 * project's receipts and reconnect snapshots, measured against the revision it last saw.
 */

const FOLDER = 'dracula';

function receipt(revision: number, facets: BookFacets[] = ['Characters'], folder = FOLDER) {
  const message: ReceiptMessage = {
    folder,
    mutationName: 'X',
    mutationId: `m${revision}`,
    revision,
    originId: 'another-browser',
    effects: {
      scope: 'Exact',
      createdId: null,
      createdParentId: null,
      facets,
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
    },
  };
  return message;
}

/** What a follower heard, in order: `r7`, `r9 gap`, `catch-up`. */
type Heard = string[];

describe('ProjectFeed', () => {
  let timers: FakeTimers;
  let connections: FakeConnection[];
  /** The revision `JoinProject` answers with. */
  let joinRevision: number;
  /** The `projects` the next `GetSnapshot` answers with. */
  let snapshotProjects: Record<string, ProjectSnapshot>;
  let live: LiveService;
  let feed: ProjectFeed;
  let handles: FollowHandle[];

  const connection = () => connections.at(-1)!;

  beforeEach(() => {
    resetServices();
    timers = new FakeTimers().install();
    connections = [];
    joinRevision = 5;
    snapshotProjects = {};
    override(LIVE_CONNECTION_FACTORY, (options: LiveConnectionOptions) => {
      const c = new FakeConnection(options);
      c.results.set('GetSnapshot', () => hubSnapshot(snapshotProjects));
      c.results.set('JoinProject', (folder) => projectSnapshot(String(folder), joinRevision));
      connections.push(c);
      return c;
    });
    override(ToastService, { warn: () => {}, success: () => {} } as unknown as ToastService);
    live = new LiveService();
    override(LiveService, live);
    feed = new ProjectFeed();
    handles = [];
  });

  afterEach(async () => {
    for (const h of handles) h.close();
    await live.stop();
    timers.restore();
  });

  function follow(folder = FOLDER, options: { baseline?: 'await' } = {}) {
    const heard: Heard = [];
    const handle = feed.follow(
      folder,
      {
        receipt: (r, { gap }) => heard.push(`r${r.revision}${gap ? ' gap' : ''}`),
        catchUp: () => heard.push('catch-up'),
      },
      options,
    );
    handles.push(handle);
    return { heard, handle };
  }

  /** Connects with `FOLDER` joined by someone else (the shell), so its live entry is seeded. */
  async function connectJoined(): Promise<void> {
    await live.joinProject(FOLDER);
    live.start();
    await settle();
  }

  /** Drops the hub and heals it; the snapshot then says the project is at `revision`. */
  async function reconnectAt(revision: number, folder = FOLDER): Promise<void> {
    snapshotProjects = { [folder]: projectSnapshot(folder, revision) };
    joinRevision = revision;
    connection().drop();
    connection().recover();
    await settle();
  }

  it('joins the project group on follow and leaves it on close', async () => {
    live.start();
    await settle();
    const { handle } = follow();
    await settle();
    expect(connection().calls('JoinProject')).toEqual([[FOLDER]]);
    handle.close();
    await settle();
    expect(connection().calls('LeaveProject')).toEqual([[FOLDER]]);
  });

  it('passes in-order receipts through whole, with no gap', async () => {
    await connectJoined();
    const received: unknown[] = [];
    handles.push(
      feed.follow(FOLDER, {
        receipt: (r, info) => received.push({ revision: r.revision, isOwn: r.isOwn, ...info }),
        catchUp: () => received.push('catch-up'),
      }),
    );
    connection().emit('receipt', receipt(6));
    connection().emit('receipt', receipt(7));
    expect(received).toEqual([
      { revision: 6, isOwn: false, gap: false },
      { revision: 7, isOwn: false, gap: false },
    ]);
  });

  it('flags a receipt that skips a revision as a gap', async () => {
    await connectJoined();
    const { heard } = follow();
    connection().emit('receipt', receipt(6));
    connection().emit('receipt', receipt(9));
    connection().emit('receipt', receipt(10));
    expect(heard).toEqual(['r6', 'r9 gap', 'r10']);
  });

  it('catches up on a reconnect snapshot ahead of what it has seen, not one level with it', async () => {
    await connectJoined();
    const { heard } = follow();

    await reconnectAt(5);
    expect(heard).toEqual([]);

    await reconnectAt(8);
    expect(heard).toEqual(['catch-up']);

    connection().emit('receipt', receipt(9));
    expect(heard).toEqual(['catch-up', 'r9']);
  });

  it('a follower opened before the hub answers takes the first JoinProject answer as its baseline', async () => {
    await live.joinProject(FOLDER);
    const { heard } = follow();
    joinRevision = 7;
    live.start();
    await settle();
    expect(live.projects()[FOLDER]?.seeded).toBe(true);

    connection().emit('receipt', receipt(8));
    expect(heard).toEqual(['r8']);
  });

  it("keeps that first answer as its baseline: a reconnect's JoinProject answer does not hide the snapshot after it", async () => {
    await live.joinProject(FOLDER);
    const { heard } = follow();
    joinRevision = 7;
    live.start();
    await settle();

    await reconnectAt(9);
    expect(heard).toEqual(['catch-up']);
  });

  it('matches the folder case-insensitively', async () => {
    await connectJoined();
    const { heard } = follow('Dracula');
    connection().emit('receipt', receipt(6, ['Characters'], 'DRACULA'));
    connection().emit('receipt', receipt(7, ['Characters'], 'another-book'));
    await reconnectAt(9, 'dracula');
    expect(heard).toEqual(['r6', 'catch-up']);
  });

  describe("with { baseline: 'await' }", () => {
    it('receipts before the baseline raise the revision seen without counting as gaps', async () => {
      await connectJoined();
      const { heard, handle } = follow(FOLDER, { baseline: 'await' });
      connection().emit('receipt', receipt(9));
      handle.baseline(5);
      connection().emit('receipt', receipt(10));
      connection().emit('receipt', receipt(12));
      expect(heard).toEqual(['r9', 'r10', 'r12 gap']);
    });

    it('holds a reconnect snapshot until the baseline, then catches up when it is ahead of the read', async () => {
      await connectJoined();
      const { heard, handle } = follow(FOLDER, { baseline: 'await' });
      await reconnectAt(7);
      await reconnectAt(8);
      // A receipt heard during the read does not cover the commit the snapshot saw.
      connection().emit('receipt', receipt(9));
      expect(heard).toEqual(['r9']);

      handle.baseline(6);
      expect(heard).toEqual(['r9', 'catch-up']);
      connection().emit('receipt', receipt(10));
      expect(heard).toEqual(['r9', 'catch-up', 'r10']);
    });

    it('a held snapshot below the read is dropped quietly', async () => {
      await connectJoined();
      const { heard, handle } = follow(FOLDER, { baseline: 'await' });
      await reconnectAt(5);
      await reconnectAt(6);
      handle.baseline(7);
      expect(heard).toEqual([]);
      // Seen became the read's revision: the next snapshot at it is not ahead either.
      await reconnectAt(7);
      expect(heard).toEqual([]);
    });

    it('a repeated baseline only ever raises the revision seen', async () => {
      await connectJoined();
      const { heard, handle } = follow(FOLDER, { baseline: 'await' });
      handle.baseline(8);
      handle.baseline(4);
      await reconnectAt(7);
      expect(heard).toEqual([]);
      handle.baseline(10);
      await reconnectAt(9);
      expect(heard).toEqual([]);
      await reconnectAt(11);
      expect(heard).toEqual(['catch-up']);
    });
  });

  describe('refetchOn', () => {
    function refetchOn(facets: Parameters<ProjectFeed['refetchOn']>[1]) {
      let reads = 0;
      handles.push(
        feed.refetchOn(FOLDER, facets, async () => {
          reads++;
        }),
      );
      return { reads: () => reads };
    }

    it('refetches once per burst of receipts with a wanted facet, and not for others', async () => {
      await connectJoined();
      const { reads } = refetchOn(['Pronunciations']);
      connection().emit('receipt', receipt(6, ['Characters']));
      await timers.advance(REFETCH_DEBOUNCE_MS);
      expect(reads()).toBe(0);

      connection().emit('receipt', receipt(7, ['Pronunciations']));
      connection().emit('receipt', receipt(8, ['Pronunciations']));
      await timers.advance(REFETCH_DEBOUNCE_MS - 1);
      expect(reads()).toBe(0);
      await timers.advance(1);
      expect(reads()).toBe(1);
    });

    it('refetches on a gap and on a catch-up, whatever the facets', async () => {
      await connectJoined();
      const { reads } = refetchOn(['Pronunciations']);
      connection().emit('receipt', receipt(7, ['Characters']));
      await timers.advance(REFETCH_DEBOUNCE_MS);
      expect(reads()).toBe(1);

      await reconnectAt(9);
      await timers.advance(REFETCH_DEBOUNCE_MS);
      expect(reads()).toBe(2);
    });

    it('reads the facets again on every receipt when given a function', async () => {
      await connectJoined();
      let facets: BookFacets[] = ['Characters'];
      const { reads } = refetchOn(() => facets);
      connection().emit('receipt', receipt(6, ['VoiceRules']));
      await timers.advance(REFETCH_DEBOUNCE_MS);
      expect(reads()).toBe(0);

      facets = ['Characters', 'VoiceRules'];
      connection().emit('receipt', receipt(7, ['VoiceRules']));
      await timers.advance(REFETCH_DEBOUNCE_MS);
      expect(reads()).toBe(1);
    });

    it('runs no opening read, and none after close', async () => {
      await connectJoined();
      const { reads } = refetchOn(['Characters']);
      await timers.advance(REFETCH_DEBOUNCE_MS);
      expect(reads()).toBe(0);

      connection().emit('receipt', receipt(6));
      handles.pop()!.close();
      await timers.advance(REFETCH_DEBOUNCE_MS);
      expect(reads()).toBe(0);
    });
  });
});
