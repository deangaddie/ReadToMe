import {
  AssemblyState,
  AttributionQueueState,
  AudioQueueState,
  EscalationState,
  QueueMessage,
  VoiceBatchState,
  WatchdogState,
} from '@app/live/live-messages';
import { JobState, JobView } from '@app/ui/job-pill/job';
import { StatusKind } from '@app/ui/status-chip/status-chip';
import { escalationBanner } from '@app/ui/stream-llm/llm-turns';

/**
 * Everything the job list is derived from (ticket 14): the hub's snapshot-shaped state plus the
 * client-side bits the hub does not carry — how long ago the queue message arrived (so elapsed
 * and ETA tick between pushes), jobs registered by feature code (the single voice-prompt run),
 * and which failed jobs the user has dismissed.
 */
export interface ActivityInputs {
  queue: QueueMessage | null;
  assembly: AssemblyState;
  voiceBatch: VoiceBatchState;
  watchdog: WatchdogState;
  /** Seconds since the current `queue` message was received. */
  sinceQueueSeconds: number;
  localJobs: readonly JobView[];
  /** Job ids whose failed card the user dismissed. */
  dismissed: ReadonlySet<string>;
}

export const ATTRIBUTION_JOB_ID = 'attribution';
export const AUDIO_JOB_ID = 'audio';
export const VOICE_BATCH_JOB_ID = 'voiceBatch';
export const ASSEMBLY_JOB_ID = 'assembly';
export const WATCHDOG_JOB_PREFIX = 'watchdog:';

/** `AssemblyPhase` member name → label (research/live-events.md §1.4). */
const ASSEMBLY_PHASE_LABEL: Record<string, string> = {
  Gather: 'Gather',
  Silence: 'Silence',
  ProbeConcat: 'Probe & concat',
  Encode: 'Encode',
  Finalize: 'Finalize',
};

export interface WatchdogKindView {
  /** Chip on the Services tab. */
  status: StatusKind;
  label: string;
  /** Job the kind stands for, or null when it is not background work (healthy / unknown). */
  job: { state: JobState; detail: string | null } | null;
}

/** One reading of each watchdog kind for the Services tab chip and the recovery/down job. */
export const WATCHDOG_KIND_VIEW: Record<string, WatchdogKindView> = {
  recoveryStarted: {
    status: 'busy',
    label: 'Recovering',
    job: { state: 'running', detail: 'Waiting for the service' },
  },
  containerRestarted: {
    status: 'busy',
    label: 'Restarted',
    job: { state: 'running', detail: 'Container restarted' },
  },
  serviceHealthy: { status: 'ok', label: 'Healthy', job: null },
  serviceDown: { status: 'error', label: 'Down', job: { state: 'failed', detail: null } },
};

export const UNKNOWN_WATCHDOG_VIEW: WatchdogKindView = {
  status: 'neutral',
  label: 'No events',
  job: null,
};

export function watchdogView(kind: string | undefined): WatchdogKindView {
  return (kind && WATCHDOG_KIND_VIEW[kind]) || UNKNOWN_WATCHDOG_VIEW;
}

export function isActiveJob(job: JobView): boolean {
  return job.state === 'running' || job.state === 'queued';
}

export function isAttributionActive(q: AttributionQueueState): boolean {
  return q.isBusy || q.queuedCount > 0 || q.processingCount > 0;
}

export function isAudioActive(q: AudioQueueState): boolean {
  return q.queuedCount > 0 || q.processingCount > 0;
}

/** Either queue has work: the client-side elapsed/ETA ticker runs while this holds. */
export function isQueueBusy(queue: QueueMessage): boolean {
  return isAttributionActive(queue.attribution) || isAudioActive(queue.audio);
}

/** Job view models in a stable order: attribution, audio, voice batch, assembly, watchdog, local. */
export function deriveJobs(inputs: ActivityInputs): JobView[] {
  const jobs: JobView[] = [];
  if (inputs.queue) {
    const attribution = attributionJob(
      inputs.queue.attribution,
      inputs.queue.escalation,
      inputs.sinceQueueSeconds,
    );
    if (attribution) jobs.push(attribution);
    const audio = audioJob(inputs.queue.audio, inputs.sinceQueueSeconds);
    if (audio) jobs.push(audio);
  }
  const batch = voiceBatchJob(inputs.voiceBatch);
  if (batch) jobs.push(batch);
  const assembly = assemblyJob(inputs.assembly, inputs.dismissed);
  if (assembly) jobs.push(assembly);
  jobs.push(...watchdogJobs(inputs.watchdog, inputs.dismissed));
  jobs.push(...inputs.localJobs);
  return jobs;
}

function attributionJob(
  q: AttributionQueueState,
  escalation: EscalationState | null | undefined,
  since: number,
): JobView | null {
  if (!isAttributionActive(q)) return null;
  return {
    id: ATTRIBUTION_JOB_ID,
    kind: 'attribution',
    label: 'Attributing',
    state: 'running',
    queued: q.queuedCount,
    processing: q.processingCount,
    completed: q.completedCount,
    rate: rate(q.averageSecondsPerParagraph, 'para'),
    etaSeconds: eta(q.estimatedSecondsRemaining, since),
    elapsedSeconds: elapsed(q.processingCount, q.currentItemElapsedSeconds, since),
    detail: escalation
      ? escalationBanner(escalation.itemCount, escalation.configName, escalation.step)
      : null,
    cancellable: true,
  };
}

function audioJob(q: AudioQueueState, since: number): JobView | null {
  if (!isAudioActive(q)) return null;
  return {
    id: AUDIO_JOB_ID,
    kind: 'audio',
    label: 'Generating audio',
    state: 'running',
    queued: q.queuedCount,
    processing: q.processingCount,
    completed: q.completedCount,
    rate: rate(q.averageSecondsPerItem, 'item'),
    etaSeconds: eta(q.estimatedSecondsRemaining, since),
    elapsedSeconds: elapsed(q.processingCount, q.currentItemElapsedSeconds, since),
    cancellable: true,
  };
}

/**
 * Only while running: the hub has no `failed` voice-batch kind (a failure surfaces as the
 * completion toast's failed count), so a failed card would exist only after a resync.
 */
function voiceBatchJob(b: VoiceBatchState): JobView | null {
  if (!b.isRunning) return null;
  return {
    id: VOICE_BATCH_JOB_ID,
    kind: 'voiceBatch',
    label: b.currentOperation || 'Voice batch',
    state: 'running',
    completed: b.processed,
    total: b.total,
    failed: b.failed,
    detail: b.currentVoiceName ?? null,
    cancellable: true,
  };
}

function assemblyJob(a: AssemblyState, dismissed: ReadonlySet<string>): JobView | null {
  const base = { id: ASSEMBLY_JOB_ID, kind: 'assembly', label: 'Assembling audiobook' } as const;
  if (a.isRunning) {
    const phase = a.currentPhase ? (ASSEMBLY_PHASE_LABEL[a.currentPhase] ?? a.currentPhase) : null;
    const detail =
      a.currentPhase === 'Encode' ? `${phase} · ${Math.round(a.encodePercent)}%` : phase;
    return { ...base, state: 'running', detail, cancellable: true };
  }
  if (a.lastError && !dismissed.has(ASSEMBLY_JOB_ID)) {
    return { ...base, state: 'failed', error: a.lastError, cancellable: false, dismissible: true };
  }
  return null;
}

function watchdogJobs(w: WatchdogState, dismissed: ReadonlySet<string>): JobView[] {
  const jobs: JobView[] = [];
  for (const [service, kind] of Object.entries(w)) {
    const view = watchdogView(kind).job;
    if (!view) continue;
    const id = `${WATCHDOG_JOB_PREFIX}${service}`;
    if (view.state === 'failed') {
      if (dismissed.has(id)) continue;
      jobs.push({
        id,
        kind: 'watchdog',
        label: `${service} down`,
        state: 'failed',
        error: 'Recovery failed',
        cancellable: false,
        dismissible: true,
      });
    } else {
      jobs.push({
        id,
        kind: 'watchdog',
        label: `Recovering ${service}`,
        state: view.state,
        detail: view.detail,
        cancellable: false,
      });
    }
  }
  return jobs;
}

function rate(secondsPer: number, unit: string): string | null {
  return secondsPer > 0 ? `${secondsPer.toFixed(1)} s/${unit}` : null;
}

function eta(estimated: number, since: number): number | null {
  return estimated > 0 ? Math.max(0, estimated - since) : null;
}

function elapsed(processing: number, current: number, since: number): number | null {
  return processing > 0 ? current + since : null;
}
