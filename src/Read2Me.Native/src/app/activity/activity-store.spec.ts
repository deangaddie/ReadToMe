import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { AssemblyApi, AttributionApi, AudioApi, VoicesApi } from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices, use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';
import type {
  AssemblyMessage,
  AssemblyState,
  LiveSnapshot,
  QueueMessage,
  ThroughputSnapshot,
  VoiceBatchMessage,
  VoiceBatchState,
  WatchdogMessage,
  WatchdogState,
} from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { FakeTimers } from '../../testing/fake-timers';
import { ActivityStore, ELAPSED_TICK_MS } from './activity-store';

/** Ported from the Angular TestBed spec, case for case: Subjects became Emitters. */
class FakeLive {
  readonly queue = signal<QueueMessage | null>(null);
  readonly assembly = signal<AssemblyState>(IDLE_ASSEMBLY);
  readonly voiceBatch = signal<VoiceBatchState>(IDLE_VOICE_BATCH);
  readonly watchdog = signal<WatchdogState>({});
  readonly throughput = signal<ThroughputSnapshot | null>(null);

  readonly queue$ = new Emitter<QueueMessage>();
  readonly assembly$ = new Emitter<AssemblyMessage>();
  readonly voiceBatch$ = new Emitter<VoiceBatchMessage>();
  readonly watchdog$ = new Emitter<WatchdogMessage>();
  readonly throughput$ = new Emitter<ThroughputSnapshot>();
  readonly resyncedSubject = new Emitter<LiveSnapshot>();

  on(family: string, listener: (m: unknown) => void) {
    switch (family) {
      case 'queue':
        return this.queue$.subscribe(listener as (m: QueueMessage) => void);
      case 'assembly':
        return this.assembly$.subscribe(listener as (m: AssemblyMessage) => void);
      case 'voiceBatch':
        return this.voiceBatch$.subscribe(listener as (m: VoiceBatchMessage) => void);
      case 'watchdog':
        return this.watchdog$.subscribe(listener as (m: WatchdogMessage) => void);
      case 'throughput':
        return this.throughput$.subscribe(listener as (m: ThroughputSnapshot) => void);
      default:
        throw new Error(`unexpected family ${family}`);
    }
  }
  resynced(listener: (s: LiveSnapshot) => void) {
    return this.resyncedSubject.subscribe(listener);
  }

  /** Mirrors LiveService: fold into the signal, then fan out. */
  pushQueue(m: QueueMessage): void {
    this.queue.set(m);
    this.queue$.emit(m);
  }
  pushAssembly(state: AssemblyState, m: AssemblyMessage): void {
    this.assembly.set(state);
    this.assembly$.emit(m);
  }
  pushVoiceBatch(state: VoiceBatchState, m: VoiceBatchMessage): void {
    this.voiceBatch.set(state);
    this.voiceBatch$.emit(m);
  }
  pushWatchdog(m: WatchdogMessage): void {
    this.watchdog.update((w) => ({ ...w, [m.service]: m.kind }));
    this.watchdog$.emit(m);
  }
  pushThroughput(t: ThroughputSnapshot): void {
    this.throughput.set(t);
    this.throughput$.emit(t);
  }
}

function busyQueue(overrides: Partial<QueueMessage['attribution']> = {}): QueueMessage {
  return {
    attribution: {
      queuedCount: 5,
      processingCount: 1,
      averageSecondsPerParagraph: 2,
      estimatedSecondsRemaining: 10,
      completedCount: 3,
      currentItemElapsedSeconds: 2,
      isBusy: true,
      ...overrides,
    },
    audio: {
      queuedCount: 0,
      processingCount: 0,
      averageSecondsPerItem: 0,
      estimatedSecondsRemaining: 0,
      completedCount: 0,
      currentItemElapsedSeconds: 0,
    },
    escalation: null,
  };
}

const IDLE_QUEUE: QueueMessage = busyQueue({
  queuedCount: 0,
  processingCount: 0,
  estimatedSecondsRemaining: 0,
  currentItemElapsedSeconds: 0,
  isBusy: false,
});

function throughput(overrides: Partial<ThroughputSnapshot> = {}): ThroughputSnapshot {
  return {
    hasRun: true,
    isRunActive: true,
    runThroughput: 40.5,
    perConfig: [{ configId: 1, configName: 'Gemma', requests: 3, tokensPerSecond: 40.5 }],
    generationRate: 41,
    generationRateHistory: [null, 10, 20],
    ...overrides,
  };
}

let timers: FakeTimers | null = null;

function setup() {
  resetServices();
  const live = new FakeLive();
  const toast = {
    success: mock(),
    info: mock(),
    warn: mock(),
    error: mock(),
    problem: mock(),
  };
  const attribution = { cancel: mock(async () => undefined), dismiss: mock(async () => undefined) };
  const audio = { cancel: mock(async () => undefined) };
  const voices = { cancelBatch: mock(async () => undefined) };
  const assembly = { cancel: mock(async () => undefined) };
  override(LiveService, live as unknown as LiveService);
  override(ToastService, toast as unknown as ToastService);
  override(AttributionApi, attribution as unknown as AttributionApi);
  override(AudioApi, audio as unknown as AudioApi);
  override(VoicesApi, voices as unknown as VoicesApi);
  override(AssemblyApi, assembly as unknown as AssemblyApi);
  const store = use(ActivityStore);
  return { live, toast, attribution, audio, voices, assembly, store };
}

beforeEach(() => {
  timers = null;
});
afterEach(() => timers?.restore());

describe('ActivityStore', () => {
  it('derives jobs from the live signals and splits active from all', () => {
    const { live, store } = setup();
    expect(store.jobs()).toEqual([]);
    expect(store.hasActive()).toBe(false);

    live.pushQueue(busyQueue());
    live.assembly.set({ ...IDLE_ASSEMBLY, lastError: 'boom' });
    expect(store.jobs().map((j) => j.id)).toEqual(['attribution', 'assembly']);
    expect(store.activeJobs().map((j) => j.id)).toEqual(['attribution']);
    expect(store.hasActive()).toBe(true);
  });

  it('ticks elapsed and ETA once a second while a queue is busy and stops when idle', async () => {
    timers = new FakeTimers().install();
    const { live, store } = setup();
    live.pushQueue(busyQueue());
    expect(store.jobs()[0]?.elapsedSeconds).toBe(2);
    expect(store.jobs()[0]?.etaSeconds).toBe(10);

    await timers.advance(ELAPSED_TICK_MS * 3);
    expect(store.jobs()[0]?.elapsedSeconds).toBe(5);
    expect(store.jobs()[0]?.etaSeconds).toBe(7);

    // A fresh queue message resets the client-side offset.
    live.pushQueue(busyQueue({ currentItemElapsedSeconds: 1, estimatedSecondsRemaining: 20 }));
    expect(store.jobs()[0]?.elapsedSeconds).toBe(1);
    await timers.advance(ELAPSED_TICK_MS);
    expect(store.jobs()[0]?.elapsedSeconds).toBe(2);
    expect(store.jobs()[0]?.etaSeconds).toBe(19);

    live.pushQueue(IDLE_QUEUE);
    expect(timers.pending).toBe(0);
    expect(store.jobs()).toEqual([]);
  });

  it('stamps the queue on resync as well as on a push', async () => {
    timers = new FakeTimers().install();
    const { live, store } = setup();
    live.queue.set(busyQueue());
    live.resyncedSubject.emit({} as LiveSnapshot);
    await timers.advance(ELAPSED_TICK_MS * 2);
    expect(store.jobs()[0]?.elapsedSeconds).toBe(4);
  });

  it('routes cancel to the right endpoint', async () => {
    const { store, attribution, audio, voices, assembly } = setup();
    await store.cancel('attribution');
    await store.cancel('audio');
    await store.cancel('voiceBatch');
    await store.cancel('assembly');
    expect(attribution.cancel).toHaveBeenCalledTimes(1);
    expect(audio.cancel).toHaveBeenCalledTimes(1);
    expect(voices.cancelBatch).toHaveBeenCalledTimes(1);
    expect(assembly.cancel).toHaveBeenCalledTimes(1);
  });

  it('dismisses a failed job locally until the job starts again', () => {
    const { live, store } = setup();
    live.assembly.set({ ...IDLE_ASSEMBLY, lastError: 'boom' });
    expect(store.jobs()).toHaveLength(1);

    store.dismiss('assembly');
    expect(store.jobs()).toEqual([]);

    live.pushAssembly(
      { ...IDLE_ASSEMBLY, isRunning: true, currentPhase: 'Gather' },
      { kind: 'phaseStarted', phase: 'Gather' },
    );
    live.pushAssembly(
      { ...IDLE_ASSEMBLY, lastError: 'again' },
      { kind: 'failed', reason: 'again' },
    );
    expect(store.jobs()[0]?.error).toBe('again');
  });

  it('shows the throughput headline during a run and the table only after it ends', () => {
    const { live, store } = setup();
    expect(store.showThroughput()).toBe(false);

    live.pushThroughput(throughput());
    expect(store.showThroughput()).toBe(true);
    expect(store.showThroughputTable()).toBe(false);
    expect(store.throughputHistory()).toEqual([0, 10, 20]);

    live.pushThroughput(throughput({ isRunActive: false }));
    expect(store.showThroughputTable()).toBe(true);
  });

  it('dismissing throughput posts to the host and hides the block until the next run', async () => {
    const { live, store, attribution } = setup();
    live.pushThroughput(throughput({ isRunActive: false }));

    await store.dismissThroughput();
    expect(attribution.dismiss).toHaveBeenCalledTimes(1);
    expect(store.showThroughput()).toBe(false);

    live.pushThroughput(throughput({ isRunActive: true }));
    expect(store.showThroughput()).toBe(true);
  });

  it('toasts foreign job completions', () => {
    const { live, toast } = setup();
    live.pushAssembly({ ...IDLE_ASSEMBLY, encodePercent: 100 }, { kind: 'completed' });
    expect(toast.success).toHaveBeenCalledWith('Assembly finished');

    live.pushAssembly(
      { ...IDLE_ASSEMBLY, lastError: 'ffmpeg' },
      { kind: 'failed', reason: 'ffmpeg' },
    );
    expect(toast.error).toHaveBeenCalledWith('Assembly failed: ffmpeg');

    live.pushVoiceBatch(
      { ...IDLE_VOICE_BATCH, processed: 12, failed: 1 },
      { kind: 'completed', processed: 12, failed: 1 },
    );
    expect(toast.success).toHaveBeenCalledWith('Voice batch complete: 12 done, 1 failed');

    live.pushVoiceBatch(IDLE_VOICE_BATCH, { kind: 'cancelled' });
    expect(toast.info).toHaveBeenCalledWith('Voice batch cancelled');

    live.pushWatchdog({ kind: 'serviceDown', service: 'llama', reason: 'x' });
    expect(toast.error).toHaveBeenCalledWith('llama is down');
  });

  it('registers local jobs with their own cancel and unregisters them', async () => {
    const { store } = setup();
    const cancel = mock();
    const unregister = store.registerLocalJob(
      { id: 'voicePrompt:c1', kind: 'voicePrompt', label: 'Voice prompt', state: 'running' },
      cancel,
    );
    expect(store.activeJobs().map((j) => j.id)).toEqual(['voicePrompt:c1']);
    expect(store.jobs()[0]?.cancellable).toBe(true);

    await store.cancel('voicePrompt:c1');
    expect(cancel).toHaveBeenCalledTimes(1);

    unregister();
    expect(store.jobs()).toEqual([]);
  });

  it('opens the drawer on a chosen tab and toggles it', () => {
    const { store } = setup();
    expect(store.drawerOpen()).toBe(false);
    store.openDrawer('llm');
    expect(store.drawerOpen()).toBe(true);
    expect(store.tab()).toBe('llm');
    store.toggleDrawer();
    expect(store.drawerOpen()).toBe(false);
  });
});
