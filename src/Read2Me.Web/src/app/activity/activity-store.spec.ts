import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { AssemblyApi, AttributionApi, AudioApi, VoicesApi } from '@app/api';
import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';
import {
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
import { ToastService } from '@app/ui/toast/toast.service';
import { ActivityStore, ELAPSED_TICK_MS } from './activity-store';

class FakeLive {
  readonly queue = signal<QueueMessage | null>(null);
  readonly assembly = signal<AssemblyState>(IDLE_ASSEMBLY);
  readonly voiceBatch = signal<VoiceBatchState>(IDLE_VOICE_BATCH);
  readonly watchdog = signal<WatchdogState>({});
  readonly throughput = signal<ThroughputSnapshot | null>(null);

  readonly queue$ = new Subject<QueueMessage>();
  readonly assembly$ = new Subject<AssemblyMessage>();
  readonly voiceBatch$ = new Subject<VoiceBatchMessage>();
  readonly watchdog$ = new Subject<WatchdogMessage>();
  readonly throughput$ = new Subject<ThroughputSnapshot>();
  readonly resynced = new Subject<LiveSnapshot>();
  readonly resynced$ = this.resynced.asObservable();

  on(family: string) {
    switch (family) {
      case 'queue':
        return this.queue$.asObservable();
      case 'assembly':
        return this.assembly$.asObservable();
      case 'voiceBatch':
        return this.voiceBatch$.asObservable();
      case 'watchdog':
        return this.watchdog$.asObservable();
      case 'throughput':
        return this.throughput$.asObservable();
      default:
        throw new Error(`unexpected family ${family}`);
    }
  }

  /** Mirrors LiveService: fold into the signal, then fan out. */
  pushQueue(m: QueueMessage): void {
    this.queue.set(m);
    this.queue$.next(m);
  }
  pushAssembly(state: AssemblyState, m: AssemblyMessage): void {
    this.assembly.set(state);
    this.assembly$.next(m);
  }
  pushVoiceBatch(state: VoiceBatchState, m: VoiceBatchMessage): void {
    this.voiceBatch.set(state);
    this.voiceBatch$.next(m);
  }
  pushWatchdog(m: WatchdogMessage): void {
    this.watchdog.update((w) => ({ ...w, [m.service]: m.kind }));
    this.watchdog$.next(m);
  }
  pushThroughput(t: ThroughputSnapshot): void {
    this.throughput.set(t);
    this.throughput$.next(t);
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

function setup() {
  const live = new FakeLive();
  const toast = {
    success: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    problem: vi.fn(),
  };
  const attribution = {
    cancel: vi.fn(async () => undefined),
    dismiss: vi.fn(async () => undefined),
  };
  const audio = { cancel: vi.fn(async () => undefined) };
  const voices = { cancelBatch: vi.fn(async () => undefined) };
  const assembly = { cancel: vi.fn(async () => undefined) };
  TestBed.configureTestingModule({
    providers: [
      { provide: LiveService, useValue: live },
      { provide: ToastService, useValue: toast },
      { provide: AttributionApi, useValue: attribution },
      { provide: AudioApi, useValue: audio },
      { provide: VoicesApi, useValue: voices },
      { provide: AssemblyApi, useValue: assembly },
    ],
  });
  const store = TestBed.inject(ActivityStore);
  return { live, toast, attribution, audio, voices, assembly, store };
}

describe('ActivityStore', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

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

  it('ticks elapsed and ETA once a second while a queue is busy and stops when idle', () => {
    vi.useFakeTimers();
    const { live, store } = setup();
    live.pushQueue(busyQueue());
    expect(store.jobs()[0]?.elapsedSeconds).toBe(2);
    expect(store.jobs()[0]?.etaSeconds).toBe(10);

    vi.advanceTimersByTime(ELAPSED_TICK_MS * 3);
    expect(store.jobs()[0]?.elapsedSeconds).toBe(5);
    expect(store.jobs()[0]?.etaSeconds).toBe(7);

    // A fresh queue message resets the client-side offset.
    live.pushQueue(busyQueue({ currentItemElapsedSeconds: 1, estimatedSecondsRemaining: 20 }));
    expect(store.jobs()[0]?.elapsedSeconds).toBe(1);
    vi.advanceTimersByTime(ELAPSED_TICK_MS);
    expect(store.jobs()[0]?.elapsedSeconds).toBe(2);
    expect(store.jobs()[0]?.etaSeconds).toBe(19);

    live.pushQueue(IDLE_QUEUE);
    expect(vi.getTimerCount()).toBe(0);
    expect(store.jobs()).toEqual([]);
  });

  it('stamps the queue on resync as well as on a push', () => {
    vi.useFakeTimers();
    const { live, store } = setup();
    live.queue.set(busyQueue());
    live.resynced.next({} as LiveSnapshot);
    vi.advanceTimersByTime(ELAPSED_TICK_MS * 2);
    expect(store.jobs()[0]?.elapsedSeconds).toBe(4);
  });

  it('routes cancel to the right endpoint', async () => {
    const { store, attribution, audio, voices, assembly } = setup();
    await store.cancel('attribution');
    await store.cancel('audio');
    await store.cancel('voiceBatch');
    await store.cancel('assembly');
    expect(attribution.cancel).toHaveBeenCalledOnce();
    expect(audio.cancel).toHaveBeenCalledOnce();
    expect(voices.cancelBatch).toHaveBeenCalledOnce();
    expect(assembly.cancel).toHaveBeenCalledOnce();
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
    expect(attribution.dismiss).toHaveBeenCalledOnce();
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
    const cancel = vi.fn();
    const unregister = store.registerLocalJob(
      { id: 'voicePrompt:c1', kind: 'voicePrompt', label: 'Voice prompt', state: 'running' },
      cancel,
    );
    expect(store.activeJobs().map((j) => j.id)).toEqual(['voicePrompt:c1']);
    expect(store.jobs()[0]?.cancellable).toBe(true);

    await store.cancel('voicePrompt:c1');
    expect(cancel).toHaveBeenCalledOnce();

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
