import type { PermanentChange } from '@app/ui/dialogs';
import type { JobView } from '@app/ui/job';

/**
 * The activity centre's permanent-change confirm (guidance-primitives spec §7): Cancel on a job
 * pill or card always asks. Neutral: cancelling loses no work, only the place in the queue. The
 * count is the work still ahead: a queue's queued plus in-flight items, a batch's total less what
 * is done; a single job (assembly, a voice prompt run) has no count and is named instead. The
 * verb is never plain "Cancel": the dialog's other button already reads that.
 */
export function cancelJobChange(job: JobView): PermanentChange {
  const n = remaining(job);
  if (n === null) {
    return {
      action: 'Cancel job',
      scope: `Cancels “${job.label}”.`,
      cost: 'Start it again to resume.',
      destructive: false,
    };
  }
  return {
    action: 'Cancel queued jobs',
    scope: `Cancels ${n} queued job${n === 1 ? '' : 's'} across all projects.`,
    cost: 'Queue them again to resume.',
    destructive: false,
  };
}

function remaining(job: JobView): number | null {
  if (job.queued !== undefined) return job.queued + (job.processing ?? 0);
  if (job.total !== undefined) {
    return Math.max(0, job.total - (job.completed ?? 0) - (job.failed ?? 0));
  }
  return null;
}
