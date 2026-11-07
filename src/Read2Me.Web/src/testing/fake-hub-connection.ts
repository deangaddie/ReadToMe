import type { LiveMessageMap, LiveSnapshot, ProjectSnapshot } from '@app/api';
import { HubConnectionState } from '@microsoft/signalr';
import type { LiveConnection, LiveConnectionOptions } from '@app/live/live-connection';
import type { LiveFamily } from '@app/live/live-messages';

type Handler = (...args: unknown[]) => void;

/**
 * A stand-in for SignalR's `HubConnection` behind `LIVE_CONNECTION_FACTORY`: no socket, the spec
 * drives it. {@link drop} and {@link recover} are an automatic reconnect, {@link close} a
 * connection lost for good, {@link emit} one frame from the hub; {@link calls} reads back the hub
 * methods the client invoked, in order.
 */
export class FakeConnection implements LiveConnection {
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

  /** One frame as the hub sends it: typed by family, so a spec's frame is the wire shape. */
  emit<K extends LiveFamily>(family: K, payload: LiveMessageMap[K]): void {
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

/** A full `GetSnapshot` answer: a busy attribution queue, idle Assembly and voice batch, and `projects`. */
export function hubSnapshot(projects: Record<string, ProjectSnapshot> = {}): LiveSnapshot {
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
      folder: null,
      outputFileName: null,
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
    watchdog: { llama: 'ServiceHealthy' },
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

/** A joined project's snapshot at `revision`, with no status yet. */
export function projectSnapshot(folder: string, revision = 1): ProjectSnapshot {
  return { folder, revision, nodes: {}, folderAudioRemaining: 0, paragraphs: {}, items: {} };
}
