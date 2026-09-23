import { EnvironmentInjector, effect, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HubConnectionState } from '@microsoft/signalr';
import { firstValueFrom, take, toArray } from 'rxjs';
import { ORIGIN_ID } from '@app/api';
import { ToastService } from '@app/ui/toast/toast.service';
import {
  LIVE_CONNECTION_FACTORY,
  LiveConnection,
  LiveConnectionOptions,
  RECONNECT_DELAYS_MS,
  reconnectDelayMs,
} from './live-connection';
import { LiveSnapshot, ProjectSnapshot, ReceiptMessage } from './live-messages';
import { DISCONNECT_TOAST_AFTER_MS, LiveService } from './live.service';

// ---- fake HubConnection --------------------------------------------------------------------------

type Handler = (...args: unknown[]) => void;

class FakeConnection implements LiveConnection {
  state = HubConnectionState.Disconnected;
  connectionId: string | null = 'conn-1';
  readonly handlers = new Map<string, Handler>();
  readonly invocations: { method: string; args: unknown[] }[] = [];
  /** Per-method canned results; a function may throw to simulate a dropped connection. */
  readonly results = new Map<string, (...args: unknown[]) => unknown>();
  /** Reject `start()` this many times before succeeding. */
  startFailures = 0;
  startCalls = 0;

  private reconnecting: ((error?: Error) => void)[] = [];
  private reconnected: ((connectionId?: string) => void)[] = [];
  private closed: ((error?: Error) => void)[] = [];

  constructor(readonly options: LiveConnectionOptions) {}

  async start(): Promise<void> {
    this.startCalls++;
    if (this.startFailures > 0) {
      this.startFailures--;
      throw new Error('host down');
    }
    this.state = HubConnectionState.Connected;
  }

  async stop(): Promise<void> {
    this.state = HubConnectionState.Disconnected;
  }

  async invoke<T = void>(method: string, ...args: unknown[]): Promise<T> {
    this.invocations.push({ method, args });
    const result = this.results.get(method);
    return (result ? result(...args) : undefined) as T;
  }

  on(method: string, handler: Handler): void {
    this.handlers.set(method, handler);
  }

  onreconnecting(cb: (error?: Error) => void): void {
    this.reconnecting.push(cb);
  }

  onreconnected(cb: (connectionId?: string) => void): void {
    this.reconnected.push(cb);
  }

  onclose(cb: (error?: Error) => void): void {
    this.closed.push(cb);
  }

  // ---- test controls ----

  emit(family: string, payload: unknown): void {
    const handler = this.handlers.get(family);
    if (!handler) throw new Error(`no handler for ${family}`);
    handler(payload);
  }

  /** Drop the transport: SignalR asks the retry policy, then fires onreconnecting. */
  drop(): void {
    this.state = HubConnectionState.Reconnecting;
    this.options.onRetry(1);
    this.reconnecting.forEach((cb) => cb(new Error('lost')));
  }

  retry(attempt: number): void {
    this.options.onRetry(attempt);
  }

  recover(): void {
    this.state = HubConnectionState.Connected;
    this.reconnected.forEach((cb) => cb('conn-2'));
  }

  close(): void {
    this.state = HubConnectionState.Disconnected;
    this.closed.forEach((cb) => cb(new Error('gone')));
  }

  calls(method: string): unknown[][] {
    return this.invocations.filter((i) => i.method === method).map((i) => i.args);
  }
}

function snapshot(projects: Record<string, ProjectSnapshot> = {}): LiveSnapshot {
  return {
    queue: {
      attribution: {
        queuedCount: 3,
        processingCount: 1,
        averageSecondsPerParagraph: 2,
        estimatedSecondsRemaining: 6,
        completedCount: 10,
        currentItemElapsedSeconds: 0.5,
        isBusy: true,
      },
      audio: {
        queuedCount: 0,
        processingCount: 0,
        averageSecondsPerItem: 0,
        estimatedSecondsRemaining: 0,
        completedCount: 0,
        currentItemElapsedSeconds: 0,
      },
      escalation: { step: 2, configName: 'Qwen 28B', itemCount: 3 },
    },
    assembly: {
      isRunning: false,
      currentPhase: null,
      encodePercent: 0,
      lastError: null,
      audioRemainingCount: 4,
    },
    voiceBatch: {
      isRunning: false,
      processed: 0,
      total: 0,
      failed: 0,
      currentVoiceName: null,
      currentOperation: null,
      lastError: null,
    },
    watchdog: { llama: 'serviceHealthy' },
    serviceStatus: { llama: 'Ready' },
    throughput: {
      hasRun: false,
      isRunActive: false,
      runThroughput: null,
      perConfig: [],
      generationRate: null,
      generationRateHistory: [],
    },
    projects,
  };
}

function project(folder: string, revision = 1): ProjectSnapshot {
  return { folder, revision, nodes: {}, folderAudioRemaining: 0, paragraphs: {}, items: {} };
}

function receipt(folder: string, originId: string): ReceiptMessage {
  return {
    folder,
    mutationName: 'CreateCharacterMutation',
    mutationId: 'm1',
    revision: 7,
    effects: {
      scope: 'Exact',
      facets: 'Characters',
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
    },
    originId,
  };
}

/** Let the service's awaited invocations settle (each `await` is a microtask hop). */
async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

// ---- tests ---------------------------------------------------------------------------------------

describe('reconnectDelayMs', () => {
  it('walks 0, 2 s, 5 s, 10 s, then stays at 30 s forever', () => {
    expect([0, 1, 2, 3, 4, 5, 50].map(reconnectDelayMs)).toEqual([
      0, 2000, 5000, 10000, 30000, 30000, 30000,
    ]);
    expect(RECONNECT_DELAYS_MS).toEqual([0, 2000, 5000, 10000, 30000]);
  });
});

describe('LiveService', () => {
  let connections: FakeConnection[];
  /** Applied to the next connection the factory builds. */
  let startFailures = 0;
  let toast: { warn: ReturnType<typeof vi.fn>; success: ReturnType<typeof vi.fn> };
  let service: LiveService;

  const current = () => connections[connections.length - 1]!;

  beforeEach(() => {
    vi.useFakeTimers();
    connections = [];
    startFailures = 0;
    toast = { warn: vi.fn(), success: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        {
          provide: LIVE_CONNECTION_FACTORY,
          useValue: (options: LiveConnectionOptions) => {
            const c = new FakeConnection(options);
            c.startFailures = startFailures;
            c.results.set('GetSnapshot', () => snapshot());
            c.results.set('JoinProject', (folder) => project(String(folder)));
            connections.push(c);
            return c;
          },
        },
        { provide: ToastService, useValue: toast },
      ],
    });
    service = TestBed.inject(LiveService);
  });

  afterEach(async () => {
    await service.stop();
    vi.useRealTimers();
  });

  it('starts disconnected, connects, and applies the snapshot', async () => {
    expect(service.state()).toBe('disconnected');
    service.start();
    expect(service.state()).toBe('connecting');
    await settle();

    expect(service.state()).toBe('connected');
    expect(service.statusLabel()).toBe('Live updates connected');
    expect(current().calls('GetSnapshot')).toHaveLength(1);
    expect(service.queue()?.attribution.queuedCount).toBe(3);
    expect(service.attributionProgress()).toEqual({
      step: 2,
      configName: 'Qwen 28B',
      itemCount: 3,
    });
    expect(service.watchdog()).toEqual({ llama: 'serviceHealthy' });
    expect(service.serviceStatus()).toEqual({ llama: 'Ready' });
    expect(service.assembly().audioRemainingCount).toBe(4);
  });

  it('registers a handler for every hub family', async () => {
    service.start();
    await settle();
    expect(Array.from(current().handlers.keys()).sort()).toEqual(
      [
        'assembly',
        'audioGen',
        'bookEdit',
        'itemStatus',
        'llm',
        'llmTest',
        'nodeStatus',
        'preflight',
        'queue',
        'receipt',
        'serviceStatus',
        'settingsChanged',
        'throughput',
        'voiceBatch',
        'watchdog',
      ].sort(),
    );
  });

  it('answers the connection id only while connected', async () => {
    expect(service.connectionId()).toBeNull();

    service.start();
    await settle();
    expect(service.connectionId()).toBe('conn-1');

    // A reconnect mints a new id, so callers must read it per request rather than hold it.
    current().connectionId = 'conn-2';
    expect(service.connectionId()).toBe('conn-2');
  });

  it('retries a failed initial start with the backoff and reports disconnected meanwhile', async () => {
    startFailures = 2;
    service.start();
    await settle();
    expect(current().startCalls).toBe(1);
    expect(service.state()).toBe('disconnected');
    expect(service.statusLabel()).toBe('Disconnected — retrying');

    await vi.advanceTimersByTimeAsync(reconnectDelayMs(0));
    await settle();
    expect(current().startCalls).toBe(2);
    expect(service.state()).toBe('disconnected');

    await vi.advanceTimersByTimeAsync(reconnectDelayMs(1));
    await settle();
    expect(current().startCalls).toBe(3);
    expect(service.state()).toBe('connected');
    expect(current().calls('GetSnapshot')).toHaveLength(1);
  });

  it('restarts the connect loop when the connection closes for good', async () => {
    service.start();
    await settle();
    current().close();
    expect(service.state()).toBe('disconnected');
    await settle();
    expect(current().startCalls).toBe(2);
    expect(service.state()).toBe('connected');
    expect(current().calls('GetSnapshot')).toHaveLength(2);
  });

  it('re-joins remembered groups and requests a fresh snapshot on reconnect', async () => {
    service.start();
    await settle();
    await service.joinProject('foundation');
    await service.joinStream('llm');
    const resynced: LiveSnapshot[] = [];
    service.resynced$.subscribe((s) => resynced.push(s));
    expect(current().calls('JoinProject')).toEqual([['foundation']]);
    expect(current().calls('JoinStream')).toEqual([['llm']]);
    expect(current().calls('GetSnapshot')).toHaveLength(1);

    current().drop();
    expect(service.state()).toBe('reconnecting');
    expect(service.statusLabel()).toBe('Reconnecting… attempt 1');
    current().retry(3);
    expect(service.statusLabel()).toBe('Reconnecting… attempt 3');

    current().results.set('GetSnapshot', () => snapshot({ foundation: project('foundation', 9) }));
    current().recover();
    await settle();

    expect(service.state()).toBe('connected');
    expect(service.attempt()).toBe(0);
    expect(current().calls('JoinProject')).toEqual([['foundation'], ['foundation']]);
    expect(current().calls('JoinStream')).toEqual([['llm'], ['llm']]);
    expect(current().calls('GetSnapshot')).toHaveLength(2);
    expect(resynced).toHaveLength(1);
    expect(service.projects()['foundation']?.revision).toBe(9);
  });

  it('reference-counts project membership and joins only while connected', async () => {
    await service.joinProject('foundation'); // before start: remembered, not invoked
    service.start();
    await settle();
    expect(current().calls('JoinProject')).toEqual([['foundation']]);

    await service.joinProject('Foundation'); // case-insensitive second reference
    await service.joinProject('foundation');
    expect(current().calls('JoinProject')).toHaveLength(1);

    await service.leaveProject('foundation');
    await service.leaveProject('FOUNDATION');
    expect(current().calls('LeaveProject')).toHaveLength(0);
    await service.leaveProject('foundation');
    expect(current().calls('LeaveProject')).toEqual([['foundation']]);
    expect(service.joinedProjects()).toEqual([]);
    expect(service.projects()['foundation']).toBeUndefined();

    await service.leaveProject('foundation'); // extra leave is a no-op
    expect(current().calls('LeaveProject')).toHaveLength(1);
  });

  it('reference-counts stream membership', async () => {
    service.start();
    await settle();
    await service.joinStream('llm');
    await service.joinStream('llm');
    await service.joinStream('audio');
    expect(current().calls('JoinStream')).toEqual([['llm'], ['audio']]);

    await service.leaveStream('llm');
    expect(current().calls('LeaveStream')).toHaveLength(0);
    await service.leaveStream('llm');
    await service.leaveStream('audio');
    expect(current().calls('LeaveStream')).toEqual([['llm'], ['audio']]);
    expect(service.joinedStreams()).toEqual([]);
  });

  it('filters receipts by folder and flags this tab’s own writes', async () => {
    service.start();
    await settle();
    const received = firstValueFrom(service.receipts$('foundation').pipe(take(2), toArray()));

    current().emit('receipt', receipt('other', ORIGIN_ID));
    current().emit('receipt', receipt('Foundation', ORIGIN_ID));
    current().emit('receipt', receipt('foundation', 'someone-else'));

    const receipts = await received;
    expect(receipts.map((r) => [r.folder, r.isOwn])).toEqual([
      ['Foundation', true],
      ['foundation', false],
    ]);
  });

  it('folds messages into the snapshot-shaped signals and the typed streams', async () => {
    service.start();
    await settle();
    await service.joinProject('foundation');
    const assemblies: string[] = [];
    service.on('assembly').subscribe((m) => assemblies.push(m.kind));

    current().emit('assembly', { kind: 'phaseStarted', phase: 'Encode' });
    current().emit('assembly', { kind: 'progress', fraction: 0.42 });
    expect(service.assembly()).toMatchObject({
      isRunning: true,
      currentPhase: 'Encode',
      encodePercent: 42,
    });
    current().emit('assembly', { kind: 'failed', reason: 'ffmpeg exited 1' });
    expect(service.assembly()).toMatchObject({ isRunning: false, lastError: 'ffmpeg exited 1' });
    expect(assemblies).toEqual(['phaseStarted', 'progress', 'failed']);

    current().emit('voiceBatch', { kind: 'started', operation: 'prompts', total: 5 });
    current().emit('voiceBatch', {
      kind: 'progress',
      processed: 2,
      total: 5,
      failed: 1,
      currentVoiceName: 'Hardin',
    });
    expect(service.voiceBatch()).toMatchObject({
      isRunning: true,
      processed: 2,
      failed: 1,
      currentVoiceName: 'Hardin',
      currentOperation: 'prompts',
    });

    current().emit('watchdog', { kind: 'serviceDown', service: 'llama', reason: 'timeout' });
    expect(service.watchdog()['llama']).toBe('serviceDown');
    current().emit('serviceStatus', {
      name: 'whisper',
      status: 'Stopped',
      op: 'shutdown',
      ok: true,
    });
    expect(service.serviceStatus()).toEqual({ llama: 'Ready', whisper: 'Stopped' });

    current().emit('nodeStatus', {
      folder: 'FOUNDATION',
      nodes: {
        n1: {
          attributionRemaining: 2,
          audioRemaining: 0,
          review: 0,
          attributionProcessing: true,
          attributionQueued: 1,
          isDone: false,
        },
      },
      folderAudioRemaining: 12,
    });
    current().emit('nodeStatus', {
      folder: 'foundation',
      nodes: { n1: null },
      folderAudioRemaining: 11,
    });
    expect(service.projects()['foundation']).toMatchObject({ nodes: {}, folderAudioRemaining: 11 });

    current().emit('itemStatus', {
      folder: 'foundation',
      paragraphs: { p1: { status: 'Queued' } },
      items: { i1: { status: 'Processing', audioVersion: 3 } },
    });
    expect(service.projects()['foundation']?.paragraphs['p1']).toEqual({ status: 'Queued' });
    expect(service.projects()['foundation']?.items['i1']?.audioVersion).toBe(3);

    current().emit('receipt', receipt('foundation', 'x'));
    expect(service.projects()['foundation']?.revision).toBe(7);
    expect(service.latest()['receipt']).toMatchObject({ mutationName: 'CreateCharacterMutation' });
  });

  it('ignores status deltas for projects it has not joined', async () => {
    service.start();
    await settle();
    current().emit('nodeStatus', { folder: 'stranger', nodes: {}, folderAudioRemaining: 1 });
    expect(service.projects()).toEqual({});
  });

  it('toasts once after 5 s disconnected and again when live updates return', async () => {
    service.start();
    await settle();
    current().drop();
    await vi.advanceTimersByTimeAsync(DISCONNECT_TOAST_AFTER_MS - 1);
    expect(toast.warn).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(toast.warn).toHaveBeenCalledWith('Live updates disconnected — retrying');

    current().recover();
    await settle();
    expect(toast.success).toHaveBeenCalledWith('Live updates reconnected');
    expect(toast.warn).toHaveBeenCalledTimes(1);
  });

  it('does not toast when the connection comes back within 5 s', async () => {
    service.start();
    await settle();
    current().drop();
    await vi.advanceTimersByTimeAsync(2000);
    current().recover();
    await settle();
    await vi.advanceTimersByTimeAsync(DISCONNECT_TOAST_AFTER_MS);
    expect(toast.warn).not.toHaveBeenCalled();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('stop() ends the loop and reports disconnected without retrying', async () => {
    service.start();
    await settle();
    await service.stop();
    expect(service.state()).toBe('disconnected');
    const calls = current().startCalls;
    await vi.advanceTimersByTimeAsync(60000);
    expect(current().startCalls).toBe(calls);
    expect(connections).toHaveLength(1);
  });
});

describe('LiveService inside an effect', () => {
  it('does not re-run a joining effect when the connection state changes', async () => {
    vi.useFakeTimers();
    const connections: FakeConnection[] = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: LIVE_CONNECTION_FACTORY,
          useValue: (options: LiveConnectionOptions) => {
            const c = new FakeConnection(options);
            c.results.set('GetSnapshot', () => snapshot());
            c.results.set('JoinProject', (folder) => project(String(folder)));
            connections.push(c);
            return c;
          },
        },
        { provide: ToastService, useValue: { warn: vi.fn(), success: vi.fn() } },
      ],
    });
    const service = TestBed.inject(LiveService);
    const injector = TestBed.inject(EnvironmentInjector);
    let runs = 0;
    runInInjectionContext(injector, () =>
      effect((onCleanup) => {
        runs++;
        void service.joinProject('foundation');
        onCleanup(() => void service.leaveProject('foundation'));
      }),
    );
    TestBed.tick();
    service.start();
    await settle();
    const c = connections[0]!;
    c.drop();
    TestBed.tick();
    c.recover();
    await settle();
    TestBed.tick();

    expect(runs).toBe(1);
    expect(c.calls('LeaveProject')).toEqual([]);
    expect(c.calls('JoinProject')).toEqual([['foundation'], ['foundation']]);
    await service.stop();
    vi.useRealTimers();
  });
});
