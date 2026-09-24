import { Injectable, OnDestroy, computed, inject, signal, untracked } from '@angular/core';
import { Observable, Subject, filter, map } from 'rxjs';
import { ORIGIN_ID } from '@app/api';
import { ToastService } from '@app/ui/toast/toast.service';
import {
  LIVE_CONNECTION_FACTORY,
  LIVE_HUB_URL,
  LiveConnection,
  reconnectDelayMs,
} from './live-connection';
import {
  AssemblyState,
  EscalationState,
  LIVE_FAMILIES,
  LIVE_HUB_METHODS,
  LiveFamily,
  LiveMessageMap,
  LiveSnapshot,
  ProjectSnapshot,
  QueueMessage,
  Receipt,
  ServiceStatusState,
  StreamKind,
  ThroughputSnapshot,
  VoiceBatchState,
  WatchdogState,
} from './live-messages';
import {
  IDLE_ASSEMBLY,
  IDLE_VOICE_BATCH,
  applyAssembly,
  applyItemStatus,
  applyNodeStatus,
  applyVoiceBatch,
  emptyProject,
} from './live-state';

export type LiveConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/** How long the hub may be unreachable before the user is told (spec "Error handling"). */
export const DISCONNECT_TOAST_AFTER_MS = 5000;

type FamilySubjects = { [K in LiveFamily]: Subject<LiveMessageMap[K]> };

interface ProjectRef {
  /** Folder as first joined; the hub compares folders case-insensitively. */
  folder: string;
  count: number;
}

/**
 * The one SignalR connection to `/hubs/live` (ticket 07; spec D5, design §9). Owns the
 * connection lifecycle and backoff, fans each family out as a typed stream, folds the
 * snapshot-shaped families into signals, and reference-counts group membership so a reconnect
 * can re-join exactly what is still wanted. Started once from the app config; the shell reads
 * {@link state} for its connection dot.
 */
@Injectable({ providedIn: 'root' })
export class LiveService implements OnDestroy {
  private readonly toast = inject(ToastService);
  private readonly createConnection = inject(LIVE_CONNECTION_FACTORY);

  private connection: LiveConnection | null = null;
  private stopped = false;
  private connectLoopRunning = false;
  private disconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private disconnectToasted = false;

  private readonly subjects = Object.fromEntries(
    LIVE_FAMILIES.map((family) => [family, new Subject()]),
  ) as unknown as FamilySubjects;
  private readonly resyncedSubject = new Subject<LiveSnapshot>();

  private readonly projectRefs = new Map<string, ProjectRef>();
  private readonly streamRefs = new Map<StreamKind, number>();

  private readonly _state = signal<LiveConnectionState>('disconnected');
  private readonly _attempt = signal(0);
  private readonly _queue = signal<QueueMessage | null>(null);
  private readonly _assembly = signal<AssemblyState>(IDLE_ASSEMBLY);
  private readonly _voiceBatch = signal<VoiceBatchState>(IDLE_VOICE_BATCH);
  private readonly _watchdog = signal<WatchdogState>({});
  private readonly _serviceStatus = signal<ServiceStatusState>({});
  private readonly _throughput = signal<ThroughputSnapshot | null>(null);
  private readonly _projects = signal<Record<string, ProjectSnapshot>>({});
  private readonly _latest = signal<Partial<Record<LiveFamily, unknown>>>({});

  readonly state = this._state.asReadonly();
  /** Reconnect attempt in progress (1-based); 0 while connected or before the first retry. */
  readonly attempt = this._attempt.asReadonly();
  /** App-bar tooltip text for the current state. */
  readonly statusLabel = computed(() => {
    switch (this._state()) {
      case 'connected':
        return 'Live updates connected';
      case 'connecting':
        return 'Connecting to live updates…';
      case 'reconnecting':
        return `Reconnecting… attempt ${this._attempt()}`;
      case 'disconnected':
        return 'Disconnected — retrying';
    }
  });

  /** Latest `queue` message (both queues + escalation), or null before the first snapshot. */
  readonly queue = this._queue.asReadonly();
  readonly assembly = this._assembly.asReadonly();
  readonly voiceBatch = this._voiceBatch.asReadonly();
  /** The attribution escalation banner, null when nothing is escalating. */
  readonly attributionProgress = computed<EscalationState | null>(
    () => this._queue()?.escalation ?? null,
  );
  /** Last known watchdog kind per service. */
  readonly watchdog = this._watchdog.asReadonly();
  /** Last observed `AiServiceStatus` per service (probes, lifecycle ops, watchdog transitions). */
  readonly serviceStatus = this._serviceStatus.asReadonly();
  readonly throughput = this._throughput.asReadonly();
  /** Current status maps per joined project (snapshot + applied deltas), keyed by folder as joined. */
  readonly projects = this._projects.asReadonly();
  /** Latest message per family — the style guide's "Live" section. */
  readonly latest = this._latest.asReadonly();

  /**
   * Fires after every (re)connect once groups are re-joined and `GetSnapshot` has been applied;
   * a view compares the snapshot's project revision with the last one it rendered.
   */
  readonly resynced$: Observable<LiveSnapshot> = this.resyncedSubject.asObservable();

  /**
   * This tab's hub connection id, or null while disconnected. An endpoint that pushes back to one
   * caller (the AI book-edit proposal run) takes it in the request body. It changes on every
   * reconnect, so it is read at call time rather than held.
   */
  connectionId(): string | null {
    return this.connection?.connectionId ?? null;
  }

  /** Typed stream of one family's messages. */
  on<K extends LiveFamily>(family: K): Observable<LiveMessageMap[K]> {
    return this.subjects[family].asObservable();
  }

  /** Receipts for one project with `isOwn` set when this tab's `X-Origin-Id` produced the write. */
  receipts$(folder: string): Observable<Receipt> {
    const key = folderKey(folder);
    return this.on('receipt').pipe(
      filter((r) => folderKey(r.folder) === key),
      map((r) => ({ ...r, isOwn: r.originId === ORIGIN_ID })),
    );
  }

  /** Opens the connection and keeps it open until {@link stop}. Idempotent. */
  start(): void {
    if (this.connection) return;
    this.stopped = false;
    const connection = this.createConnection({
      url: LIVE_HUB_URL,
      onRetry: (attempt) => this._attempt.set(attempt),
    });
    this.connection = connection;
    this.registerHandlers(connection);
    connection.onreconnecting(() => this.onLost('reconnecting'));
    connection.onreconnected(() => void this.onConnected());
    connection.onclose(() => {
      if (this.stopped) return;
      this.onLost('disconnected');
      void this.connectLoop(false);
    });
    this.armDisconnectToast();
    void this.connectLoop(true);
  }

  async stop(): Promise<void> {
    this.stopped = true;
    this.clearDisconnectToast();
    const connection = this.connection;
    this.connection = null;
    this._state.set('disconnected');
    if (connection) {
      try {
        await connection.stop();
      } catch {
        // Already closed.
      }
    }
  }

  ngOnDestroy(): void {
    void this.stop();
  }

  // ---- groups ----------------------------------------------------------------------------------

  /** Reference-counted: the first caller joins `project:{folder}`, later callers only count. */
  async joinProject(folder: string): Promise<void> {
    const key = folderKey(folder);
    const ref = this.projectRefs.get(key);
    if (ref) {
      ref.count++;
      return;
    }
    this.projectRefs.set(key, { folder, count: 1 });
    this._projects.update((all) => ({ ...all, [folder]: all[folder] ?? emptyProject(folder) }));
    if (this.isConnected()) await this.invokeJoinProject(folder);
  }

  async leaveProject(folder: string): Promise<void> {
    const key = folderKey(folder);
    const ref = this.projectRefs.get(key);
    if (!ref) return;
    if (--ref.count > 0) return;
    this.projectRefs.delete(key);
    this._projects.update((all) => {
      const next = { ...all };
      delete next[ref.folder];
      return next;
    });
    if (this.isConnected()) {
      await this.tryInvoke(LIVE_HUB_METHODS.leaveProject, ref.folder);
    }
  }

  /** Reference-counted: only the activity drawer's stream tabs call this, while visible. */
  async joinStream(kind: StreamKind): Promise<void> {
    const count = (this.streamRefs.get(kind) ?? 0) + 1;
    this.streamRefs.set(kind, count);
    if (count === 1 && this.isConnected()) {
      await this.tryInvoke(LIVE_HUB_METHODS.joinStream, kind);
    }
  }

  async leaveStream(kind: StreamKind): Promise<void> {
    const count = this.streamRefs.get(kind) ?? 0;
    if (count === 0) return;
    if (count > 1) {
      this.streamRefs.set(kind, count - 1);
      return;
    }
    this.streamRefs.delete(kind);
    if (this.isConnected()) {
      await this.tryInvoke(LIVE_HUB_METHODS.leaveStream, kind);
    }
  }

  /**
   * Untracked so a caller inside an `effect` (the shell joins per route) does not make that effect
   * depend on the connection state and re-run join/leave on every reconnect.
   */
  private isConnected(): boolean {
    return untracked(this._state) === 'connected';
  }

  /** Folders currently held by at least one reference (as first joined). */
  joinedProjects(): string[] {
    return Array.from(this.projectRefs.values(), (r) => r.folder);
  }

  joinedStreams(): StreamKind[] {
    return Array.from(this.streamRefs.keys());
  }

  // ---- connection lifecycle ----------------------------------------------------------------------

  /** Initial connect shows amber "connecting"; a restart after a close stays red until it succeeds. */
  private async connectLoop(initial: boolean): Promise<void> {
    if (this.connectLoopRunning) return;
    this.connectLoopRunning = true;
    try {
      let failures = 0;
      while (!this.stopped && this.connection) {
        const connection = this.connection;
        this._state.set(initial && failures === 0 ? 'connecting' : 'disconnected');
        try {
          await connection.start();
          if (this.connection !== connection) return;
          await this.onConnected();
          return;
        } catch {
          failures++;
          this._attempt.set(failures);
          this._state.set('disconnected');
          await delay(reconnectDelayMs(failures - 1));
        }
      }
    } finally {
      this.connectLoopRunning = false;
    }
  }

  /** After start or automatic reconnect: re-join remembered groups, resync, tell views. */
  private async onConnected(): Promise<void> {
    this._state.set('connected');
    this._attempt.set(0);
    this.clearDisconnectToast();
    if (this.disconnectToasted) {
      this.disconnectToasted = false;
      this.toast.success('Live updates reconnected');
    }
    for (const folder of this.joinedProjects()) await this.invokeJoinProject(folder);
    for (const kind of this.joinedStreams())
      await this.tryInvoke(LIVE_HUB_METHODS.joinStream, kind);
    await this.refreshSnapshot();
  }

  private onLost(state: 'reconnecting' | 'disconnected'): void {
    this._state.set(state);
    if (state === 'reconnecting' && this._attempt() === 0) this._attempt.set(1);
    this.armDisconnectToast();
  }

  private armDisconnectToast(): void {
    if (this.disconnectTimer) return;
    this.disconnectTimer = setTimeout(() => {
      this.disconnectTimer = null;
      if (this._state() === 'connected' || this.stopped) return;
      this.disconnectToasted = true;
      this.toast.warn('Live updates disconnected — retrying');
    }, DISCONNECT_TOAST_AFTER_MS);
  }

  private clearDisconnectToast(): void {
    if (this.disconnectTimer) clearTimeout(this.disconnectTimer);
    this.disconnectTimer = null;
  }

  private async invokeJoinProject(folder: string): Promise<void> {
    const snapshot = await this.tryInvoke<ProjectSnapshot>(LIVE_HUB_METHODS.joinProject, folder);
    if (snapshot) this.setProject(folder, snapshot);
  }

  private async refreshSnapshot(): Promise<void> {
    const snapshot = await this.tryInvoke<LiveSnapshot>(LIVE_HUB_METHODS.getSnapshot);
    if (!snapshot) return;
    this._queue.set(snapshot.queue);
    this._assembly.set(snapshot.assembly);
    this._voiceBatch.set(snapshot.voiceBatch);
    this._watchdog.set(snapshot.watchdog ?? {});
    this._serviceStatus.set(snapshot.serviceStatus ?? {});
    this._throughput.set(snapshot.throughput);
    for (const [folder, project] of Object.entries(snapshot.projects ?? {})) {
      this.setProject(folder, project);
    }
    this.resyncedSubject.next(snapshot);
  }

  /** Invokes when connected; a failure (connection dropped mid-call) is left to the next resync. */
  private async tryInvoke<T = void>(method: string, ...args: unknown[]): Promise<T | undefined> {
    const connection = this.connection;
    if (!connection) return undefined;
    try {
      return await connection.invoke<T>(method, ...args);
    } catch (error) {
      console.warn(`live hub ${method} failed`, error);
      return undefined;
    }
  }

  // ---- inbound -----------------------------------------------------------------------------------

  private registerHandlers(connection: LiveConnection): void {
    for (const family of LIVE_FAMILIES) {
      connection.on(family, (message: LiveMessageMap[typeof family]) =>
        this.receive(family, message),
      );
    }
  }

  private receive<K extends LiveFamily>(family: K, message: LiveMessageMap[K]): void {
    this._latest.update((all) => ({ ...all, [family]: message }));
    this.fold(family, message);
    (this.subjects[family] as Subject<LiveMessageMap[K]>).next(message);
  }

  private fold<K extends LiveFamily>(family: K, message: LiveMessageMap[K]): void {
    switch (family) {
      case 'queue':
        this._queue.set(message as LiveMessageMap['queue']);
        break;
      case 'assembly':
        this._assembly.update((s) => applyAssembly(s, message as LiveMessageMap['assembly']));
        break;
      case 'voiceBatch':
        this._voiceBatch.update((s) => applyVoiceBatch(s, message as LiveMessageMap['voiceBatch']));
        break;
      case 'watchdog': {
        const m = message as LiveMessageMap['watchdog'];
        this._watchdog.update((w) => ({ ...w, [m.service]: m.kind }));
        break;
      }
      case 'serviceStatus': {
        const m = message as LiveMessageMap['serviceStatus'];
        this._serviceStatus.update((s) => ({ ...s, [m.name]: m.status }));
        break;
      }
      case 'throughput':
        this._throughput.set(message as LiveMessageMap['throughput']);
        break;
      case 'nodeStatus': {
        const m = message as LiveMessageMap['nodeStatus'];
        this.updateProject(m.folder, (p) => applyNodeStatus(p, m));
        break;
      }
      case 'itemStatus': {
        const m = message as LiveMessageMap['itemStatus'];
        this.updateProject(m.folder, (p) => applyItemStatus(p, m));
        break;
      }
      case 'receipt': {
        const m = message as LiveMessageMap['receipt'];
        this.updateProject(m.folder, (p) => ({ ...p, revision: Math.max(p.revision, m.revision) }));
        break;
      }
      default:
        break;
    }
  }

  private setProject(folder: string, snapshot: ProjectSnapshot): void {
    const key = this.projectKeyFor(folder);
    if (!key) return;
    this._projects.update((all) => ({ ...all, [key]: snapshot }));
  }

  private updateProject(folder: string, change: (p: ProjectSnapshot) => ProjectSnapshot): void {
    const key = this.projectKeyFor(folder);
    if (!key) return;
    this._projects.update((all) => ({ ...all, [key]: change(all[key] ?? emptyProject(key)) }));
  }

  /** The folder spelling this client joined with, or null when the project is not joined. */
  private projectKeyFor(folder: string): string | null {
    return this.projectRefs.get(folderKey(folder))?.folder ?? null;
  }
}

function folderKey(folder: string): string {
  return folder.toLowerCase();
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
