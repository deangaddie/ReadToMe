import { StatusKind } from '@app/ui/status-chip/status-chip';

/** Job view model consumed by `r2m-job-pill` and `r2m-job-card` (design §5, §7). Ticket 14 derives it. */
export type JobKind =
  'attribution' | 'audio' | 'voiceBatch' | 'assembly' | 'watchdog' | 'voicePrompt';
export type JobState = 'running' | 'queued' | 'done' | 'failed' | 'cancelled';

export interface JobView {
  id: string;
  kind: JobKind;
  label: string;
  state: JobState;
  queued?: number;
  processing?: number;
  completed?: number;
  failed?: number;
  total?: number;
  etaSeconds?: number | null;
  elapsedSeconds?: number | null;
  /** Pre-formatted rate, e.g. `3.1 s/para`. */
  rate?: string | null;
  detail?: string | null;
  error?: string | null;
  cancellable?: boolean;
  dismissible?: boolean;
}

export const JOB_ICONS: Record<JobKind, string> = {
  attribution: 'person_search',
  audio: 'graphic_eq',
  voiceBatch: 'record_voice_over',
  assembly: 'library_music',
  watchdog: 'health_and_safety',
  voicePrompt: 'auto_awesome',
};

export const JOB_STATE_STATUS: Record<JobState, StatusKind> = {
  running: 'busy',
  queued: 'neutral',
  done: 'ok',
  failed: 'error',
  cancelled: 'neutral',
};

/** `0:38`, `12:04`, `1:02:09`. Negative or non-finite input renders as `0:00`. */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '0:00';
  const total = Math.floor(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const mm = h > 0 ? String(m).padStart(2, '0') : String(m);
  return `${h > 0 ? `${h}:` : ''}${mm}:${String(s).padStart(2, '0')}`;
}

/** Compact summary fragments for the pill, e.g. `['12 queued', '3.1 s/para', 'ETA 0:38']`. */
export function jobSummary(job: JobView): string[] {
  const parts: string[] = [];
  if (job.queued != null) parts.push(`${job.queued} queued`);
  if (job.processing) parts.push(`${job.processing} running`);
  if (job.completed != null && job.total != null) parts.push(`${job.completed}/${job.total}`);
  else if (job.completed != null) parts.push(`${job.completed} done`);
  if (job.failed) parts.push(`${job.failed} failed`);
  if (job.rate) parts.push(job.rate);
  if (job.etaSeconds != null && job.state === 'running')
    parts.push(`ETA ${formatDuration(job.etaSeconds)}`);
  if (parts.length === 0 && job.detail) parts.push(job.detail);
  return parts;
}
