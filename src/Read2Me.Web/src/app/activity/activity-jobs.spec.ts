import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';
import { AttributionQueueState, AudioQueueState, QueueMessage } from '@app/live/live-messages';
import { ActivityInputs, deriveJobs, isActiveJob } from './activity-jobs';

function attribution(overrides: Partial<AttributionQueueState> = {}): AttributionQueueState {
  return {
    queuedCount: 0,
    processingCount: 0,
    averageSecondsPerParagraph: 0,
    estimatedSecondsRemaining: 0,
    completedCount: 0,
    currentItemElapsedSeconds: 0,
    isBusy: false,
    ...overrides,
  };
}

function audio(overrides: Partial<AudioQueueState> = {}): AudioQueueState {
  return {
    queuedCount: 0,
    processingCount: 0,
    averageSecondsPerItem: 0,
    estimatedSecondsRemaining: 0,
    completedCount: 0,
    currentItemElapsedSeconds: 0,
    ...overrides,
  };
}

function queue(overrides: Partial<QueueMessage> = {}): QueueMessage {
  return { attribution: attribution(), audio: audio(), escalation: null, ...overrides };
}

function inputs(overrides: Partial<ActivityInputs> = {}): ActivityInputs {
  return {
    queue: queue(),
    assembly: IDLE_ASSEMBLY,
    voiceBatch: IDLE_VOICE_BATCH,
    watchdog: {},
    sinceQueueSeconds: 0,
    localJobs: [],
    dismissed: new Set(),
    ...overrides,
  };
}

describe('deriveJobs', () => {
  it('is empty when nothing is running', () => {
    expect(deriveJobs(inputs())).toEqual([]);
    expect(deriveJobs(inputs({ queue: null }))).toEqual([]);
  });

  it('derives the attribution job with counts, rate, ETA and escalation banner', () => {
    const jobs = deriveJobs(
      inputs({
        queue: queue({
          attribution: attribution({
            isBusy: true,
            queuedCount: 12,
            processingCount: 1,
            completedCount: 30,
            averageSecondsPerParagraph: 3.14,
            estimatedSecondsRemaining: 38,
            currentItemElapsedSeconds: 2,
          }),
          escalation: { step: 2, configName: 'Qwen 28B', itemCount: 3 },
        }),
      }),
    );

    expect(jobs).toHaveLength(1);
    expect(jobs[0]).toMatchObject({
      id: 'attribution',
      kind: 'attribution',
      state: 'running',
      queued: 12,
      processing: 1,
      completed: 30,
      rate: '3.1 s/para',
      etaSeconds: 38,
      elapsedSeconds: 2,
      detail: 'Escalating 3 items → Qwen 28B (step 2)',
      cancellable: true,
    });
  });

  it('ticks elapsed up and ETA down by the seconds since the queue message arrived', () => {
    const [job] = deriveJobs(
      inputs({
        queue: queue({
          attribution: attribution({
            isBusy: true,
            processingCount: 1,
            estimatedSecondsRemaining: 10,
            currentItemElapsedSeconds: 2,
          }),
        }),
        sinceQueueSeconds: 4,
      }),
    );
    expect(job?.elapsedSeconds).toBe(6);
    expect(job?.etaSeconds).toBe(6);

    const [late] = deriveJobs(
      inputs({
        queue: queue({
          attribution: attribution({
            isBusy: true,
            processingCount: 1,
            estimatedSecondsRemaining: 10,
          }),
        }),
        sinceQueueSeconds: 30,
      }),
    );
    expect(late?.etaSeconds).toBe(0);
  });

  it('does not tick elapsed while nothing is processing (queued only)', () => {
    const [job] = deriveJobs(
      inputs({
        queue: queue({ attribution: attribution({ isBusy: true, queuedCount: 3 }) }),
        sinceQueueSeconds: 5,
      }),
    );
    expect(job?.elapsedSeconds).toBeNull();
    expect(job?.rate).toBeNull();
  });

  it('derives the audio job with s/item', () => {
    const [job] = deriveJobs(
      inputs({
        queue: queue({
          audio: audio({
            queuedCount: 4,
            processingCount: 1,
            completedCount: 7,
            averageSecondsPerItem: 12.06,
            estimatedSecondsRemaining: 60,
          }),
        }),
      }),
    );
    expect(job).toMatchObject({
      id: 'audio',
      kind: 'audio',
      state: 'running',
      queued: 4,
      processing: 1,
      completed: 7,
      rate: '12.1 s/item',
      etaSeconds: 60,
      cancellable: true,
    });
  });

  it('derives the voice batch job from the operation and progress', () => {
    const [job] = deriveJobs(
      inputs({
        voiceBatch: {
          isRunning: true,
          processed: 3,
          total: 12,
          failed: 1,
          currentVoiceName: 'Hari Seldon',
          currentOperation: 'Generating prompts',
          lastError: null,
        },
      }),
    );
    expect(job).toMatchObject({
      id: 'voiceBatch',
      kind: 'voiceBatch',
      label: 'Generating prompts',
      state: 'running',
      completed: 3,
      total: 12,
      failed: 1,
      detail: 'Hari Seldon',
      cancellable: true,
    });
  });

  it('derives the assembly job with phase and encode percent', () => {
    const [job] = deriveJobs(
      inputs({
        assembly: {
          ...IDLE_ASSEMBLY,
          isRunning: true,
          currentPhase: 'Encode',
          encodePercent: 42.4,
        },
      }),
    );
    expect(job).toMatchObject({
      id: 'assembly',
      kind: 'assembly',
      state: 'running',
      detail: 'Encode · 42%',
      cancellable: true,
    });

    const [gather] = deriveJobs(
      inputs({ assembly: { ...IDLE_ASSEMBLY, isRunning: true, currentPhase: 'ProbeConcat' } }),
    );
    expect(gather?.detail).toBe('Probe & concat');
  });

  it('keeps a failed assembly as a dismissible failed job until dismissed', () => {
    const failed = { ...IDLE_ASSEMBLY, lastError: 'ffmpeg exited 1' };
    const [job] = deriveJobs(inputs({ assembly: failed }));
    expect(job).toMatchObject({
      id: 'assembly',
      state: 'failed',
      error: 'ffmpeg exited 1',
      dismissible: true,
      cancellable: false,
    });
    expect(deriveJobs(inputs({ assembly: failed, dismissed: new Set(['assembly']) }))).toEqual([]);
  });

  it('derives one watchdog job per recovering or down service', () => {
    const jobs = deriveJobs(
      inputs({
        watchdog: {
          llama: 'recoveryStarted',
          chatterbox: 'containerRestarted',
          whisper: 'serviceDown',
          minilm: 'serviceHealthy',
        },
      }),
    );
    expect(jobs.map((j) => [j.id, j.state, j.label])).toEqual([
      ['watchdog:llama', 'running', 'Recovering llama'],
      ['watchdog:chatterbox', 'running', 'Recovering chatterbox'],
      ['watchdog:whisper', 'failed', 'whisper down'],
    ]);
    expect(jobs[0]?.cancellable).toBe(false);
    expect(jobs[2]?.dismissible).toBe(true);
  });

  it('appends local jobs (the single voice-prompt run) and orders jobs by kind', () => {
    const jobs = deriveJobs(
      inputs({
        queue: queue({ audio: audio({ queuedCount: 1 }) }),
        assembly: { ...IDLE_ASSEMBLY, isRunning: true, currentPhase: 'Gather' },
        localJobs: [
          { id: 'voicePrompt:c1', kind: 'voicePrompt', label: 'Voice prompt', state: 'running' },
        ],
      }),
    );
    expect(jobs.map((j) => j.id)).toEqual(['audio', 'assembly', 'voicePrompt:c1']);
  });
});

describe('isActiveJob', () => {
  it('counts running and queued jobs as active', () => {
    const base = { id: 'x', kind: 'audio', label: 'x' } as const;
    expect(isActiveJob({ ...base, state: 'running' })).toBe(true);
    expect(isActiveJob({ ...base, state: 'queued' })).toBe(true);
    expect(isActiveJob({ ...base, state: 'failed' })).toBe(false);
    expect(isActiveJob({ ...base, state: 'done' })).toBe(false);
  });
});
