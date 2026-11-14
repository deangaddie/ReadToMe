import type {
  AssemblyState,
  LiveMessageMap,
  LiveSnapshot,
  QueueMessage,
  ThroughputSnapshot,
  VoiceBatchState,
} from '@app/api';
import { LiveService, type LiveConnectionState } from '@app/live/live.service';
import { Emitter } from '@app/core/emitter';
import { override } from '@app/core/services';
import { signal } from '@app/core/signals';
import type {
  LiveFamily,
  Receipt,
  ServiceStatusState,
  StreamKind,
  WatchdogState,
} from '@app/live/live-messages';
import { entryFor, folderKey, keyFor } from '@app/live/folders';
import {
  IDLE_ASSEMBLY,
  IDLE_VOICE_BATCH,
  type LiveProject,
  applyItemStatus,
  applyNodeStatus,
  emptyProject,
} from '@app/live/live-state';

/**
 * The slice of `LiveService` the shell, the project store and the activity centre read, with
 * group membership recorded. Streams are real Emitters: a spec pushes a hub message with
 * {@link emit} (fold the matching state signal first, as `LiveService` does; `nodeStatus` and
 * `itemStatus` fold into {@link projects} here) and a resync with {@link resync}. A joined project
 * has an unseeded entry until {@link seed}.
 */
export class FakeLive {
  readonly state = signal<LiveConnectionState>('connected');
  readonly statusLabel = () => (this.state() === 'connected' ? 'Live updates connected' : 'Lost');
  /** Joined projects as the hub answered for them; {@link seed} one so a `ProjectFeed` follower has its baseline. */
  readonly projects = signal<Record<string, LiveProject>>({});
  readonly queue = signal<QueueMessage | null>(null);
  readonly assembly = signal<AssemblyState>(IDLE_ASSEMBLY);
  readonly voiceBatch = signal<VoiceBatchState>(IDLE_VOICE_BATCH);
  readonly watchdog = signal<WatchdogState>({});
  readonly serviceStatus = signal<ServiceStatusState>({});
  readonly throughput = signal<ThroughputSnapshot | null>(null);
  /** Project groups joined and left: reference-counted by folder, as `LiveService` does. */
  readonly joined: string[] = [];
  readonly left: string[] = [];
  readonly #projectRefs = new Map<string, number>();
  readonly joinedStreams: StreamKind[] = [];
  readonly leftStreams: StreamKind[] = [];

  readonly #streams = new Map<string, Emitter<unknown>>();
  readonly #resynced = new Emitter<LiveSnapshot>();

  /** `never` lets any family's typed listener in, as `LiveService.on<K>` does. */
  on(family: string, listener: (message: never) => void) {
    return this.stream(family).subscribe(listener as (message: unknown) => void);
  }
  /** Delivers one message to every `on(family)` listener. */
  emit<K extends LiveFamily>(family: K, message: LiveMessageMap[K]): void {
    if (family === 'nodeStatus') {
      const m = message as LiveMessageMap['nodeStatus'];
      this.updateProject(m.folder, (p) => applyNodeStatus(p, m));
    } else if (family === 'itemStatus') {
      const m = message as LiveMessageMap['itemStatus'];
      this.updateProject(m.folder, (p) => applyItemStatus(p, m));
    }
    this.stream(family).emit(message);
  }
  private updateProject(folder: string, change: (p: LiveProject) => LiveProject): void {
    const all = this.projects();
    const key = keyFor(all, folder);
    if (key !== undefined) this.projects.set({ ...all, [key]: change(all[key]!) });
  }
  private stream(family: string): Emitter<unknown> {
    let stream = this.#streams.get(family);
    if (!stream) this.#streams.set(family, (stream = new Emitter<unknown>()));
    return stream;
  }
  /** A project's receipts: what a spec pushes with `emit('receipt', receipt)` for that folder. */
  receipts(folder: string, listener: (receipt: Receipt) => void) {
    return this.stream('receipt').subscribe((r) => {
      if ((r as Receipt).folder === folder) listener(r as Receipt);
    });
  }
  resynced(listener: (snapshot: LiveSnapshot) => void) {
    return this.#resynced.subscribe(listener);
  }
  /** Delivers a hub snapshot to every `resynced` listener, as a (re)connect does. */
  resync(snapshot = {} as LiveSnapshot): void {
    this.#resynced.emit(snapshot);
  }
  /** The hub answered `JoinProject` for `folder` at `revision`. */
  seed(folder: string, revision = 1): void {
    this.projects.update((all) => ({
      ...all,
      [folder]: {
        folder,
        revision,
        nodes: {},
        folderAudioRemaining: 0,
        paragraphs: {},
        items: {},
        seeded: true,
      },
    }));
  }
  connectionId(): string | null {
    return 'fake-connection';
  }
  async joinProject(folder: string) {
    const key = folderKey(folder);
    const count = (this.#projectRefs.get(key) ?? 0) + 1;
    this.#projectRefs.set(key, count);
    if (count > 1) return;
    this.joined.push(folder);
    this.projects.update((all) => ({
      ...all,
      [keyFor(all, folder) ?? folder]: entryFor(all, folder) ?? emptyProject(folder),
    }));
  }
  async leaveProject(folder: string) {
    const key = folderKey(folder);
    const count = this.#projectRefs.get(key);
    if (!count) return;
    if (count > 1) {
      this.#projectRefs.set(key, count - 1);
      return;
    }
    this.#projectRefs.delete(key);
    this.left.push(folder);
    this.projects.update((all) => {
      const next = { ...all };
      const k = keyFor(next, folder);
      if (k !== undefined) delete next[k];
      return next;
    });
  }
  async joinStream(kind: StreamKind) {
    this.joinedStreams.push(kind);
  }
  async leaveStream(kind: StreamKind) {
    this.leftStreams.push(kind);
  }

  /** Makes this the app-wide `LiveService`. */
  install(): this {
    override(LiveService, this as unknown as LiveService);
    return this;
  }
}
