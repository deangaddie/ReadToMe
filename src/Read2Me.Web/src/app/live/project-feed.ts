import type { BookFacets, LiveSnapshot } from '@app/api';
import { use } from '@app/core/services';
import { effect, untracked } from '@app/core/signals';
import { Debounced } from '@app/shared/debounced';
import { type Receipt, hasFacet } from './live-messages';
import { LiveService, type Unsubscribe } from './live.service';

/** What a follower hears from its {@link ProjectFeed}. */
export interface ProjectFollower {
  /** A receipt for the project, whole; `gap` when it skips a revision past the one seen. */
  receipt(receipt: Receipt, info: { gap: boolean }): void;
  /** A reconnect snapshot is ahead of what this follower has seen: read again what it shows. */
  catchUp(): void;
}

export interface FollowOptions {
  /**
   * `'await'`: the follower's own read sets the baseline through {@link FollowHandle.baseline}.
   * Until then reconnect snapshots are held and receipts raise the revision seen without counting
   * as gaps. Without it the baseline is the project's live revision once the hub has answered.
   */
  baseline?: 'await';
}

/** One follower's following of one project. */
export interface FollowHandle {
  /** The follower read the project at `revision`; only ever raises the revision seen. */
  baseline(revision: number): void;
  /** Stops following and leaves the project group. */
  close(): void;
}

/** The facets that make a {@link ProjectFeed.refetchOn} follower read again. */
export type RefetchFacets = readonly BookFacets[] | (() => readonly BookFacets[]);

/**
 * The Project feed (context/web.md): one view's following of one project's revisions, so none
 * misses what changed while the hub was away. Each {@link follow} is its own handle, since two
 * followers of a folder read at different revisions; the feed owns the shared parts — the
 * case-insensitive folder match, the receipt filter, the snapshot lookup and the revision rule.
 * It knows nothing of History steps; followers' reads carry `?at=` themselves.
 */
export class ProjectFeed {
  private readonly live = use(LiveService);

  follow(folder: string, follower: ProjectFollower, options: FollowOptions = {}): FollowHandle {
    const live = this.live;
    const key = folderKey(folder);
    /** Highest revision accounted for; null before the baseline and any receipt. */
    let seen: number | null = null;
    let baselined = false;
    /** The highest snapshot revision that arrived before the baseline. */
    let held: number | null = null;
    let closed = false;

    const baseline = (revision: number): void => {
      if (closed) return;
      const pending = held;
      held = null;
      baselined = true;
      seen = Math.max(seen ?? revision, revision, pending ?? revision);
      // A held snapshot is compared with the read's revision, not with receipts heard during the
      // read: a commit made before the hub joined sends no receipt, so a later one does not cover it.
      if (pending !== null && pending > revision) follower.catchUp();
    };

    const onReceipt = (r: Receipt): void => {
      const gap = baselined && seen !== null && r.revision > seen + 1;
      if (seen === null || r.revision > seen) seen = r.revision;
      follower.receipt(r, { gap });
    };

    const onResync = (snapshot: LiveSnapshot): void => {
      const revision = revisionIn(snapshot.projects, key);
      if (revision === undefined) return;
      if (!baselined) {
        held = Math.max(held ?? revision, revision);
        return;
      }
      if (seen !== null && revision <= seen) return;
      seen = revision;
      follower.catchUp();
    };

    const stops: Unsubscribe[] = [live.receipts(folder, onReceipt), live.resynced(onResync)];
    void live.joinProject(folder);
    if (options.baseline !== 'await') {
      // The live revision once the hub has answered for this project: now, or on the first answer.
      // Only the first: a later `JoinProject` answer (a reconnect) must not lift the baseline past
      // the snapshot that follows it.
      let adopted = false;
      stops.push(
        effect(() => {
          const entry = entryIn(live.projects(), key);
          if (adopted || !entry?.seeded) return;
          adopted = true;
          untracked(() => baseline(entry.revision));
        }),
      );
    }

    return {
      baseline,
      close: () => {
        if (closed) return;
        closed = true;
        for (const stop of stops) stop();
        void live.leaveProject(folder);
      },
    };
  }

  /**
   * One debounced refetch whenever `folder` changes in a way the view shows: a receipt carrying
   * any of `facets` (a function when the set changes over time), a gap, or a catch-up. It never
   * runs the opening read; the caller keeps its own first load. `options` as for {@link follow}.
   */
  refetchOn(
    folder: string,
    facets: RefetchFacets,
    refetch: () => Promise<void>,
    options: FollowOptions = {},
  ): FollowHandle {
    const debounced = new Debounced(refetch);
    const wanted = typeof facets === 'function' ? facets : () => facets;
    const handle = this.follow(
      folder,
      {
        receipt: (r, { gap }) => {
          if (gap || wanted().some((f) => hasFacet(r.effects.facets, f))) debounced.schedule();
        },
        catchUp: () => debounced.schedule(),
      },
      options,
    );
    return {
      baseline: (revision) => handle.baseline(revision),
      close: () => {
        debounced.cancel();
        handle.close();
      },
    };
  }
}

function folderKey(folder: string): string {
  return folder.toLowerCase();
}

function entryIn<T>(byFolder: Record<string, T> | null | undefined, key: string): T | undefined {
  const match = Object.keys(byFolder ?? {}).find((k) => folderKey(k) === key);
  return match === undefined ? undefined : byFolder?.[match];
}

function revisionIn(
  projects: LiveSnapshot['projects'] | null | undefined,
  key: string,
): number | undefined {
  return entryIn(projects, key)?.revision;
}
