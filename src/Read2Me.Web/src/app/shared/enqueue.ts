import { toApiError } from '@app/api';
import { use } from '@app/core/services';
import type { WritableSignal } from '@app/core/signals';
import { ToastService } from '@app/ui/toast';
import { Preflight, type PreflightTask } from './preflight';

export interface EnqueueOptions {
  /** The caller's busy flag: set for the whole call, and a call while it is set does nothing. */
  busy: WritableSignal<boolean>;
  /** The preflight that must pass before anything is sent. */
  task: PreflightTask;
  /** What the toast counts, singular ("item", "paragraph"). */
  unit: string;
  /** The enqueue itself; the caller has already checked its folder. */
  request: () => Promise<{ enqueued: number }>;
}

/**
 * Preflight → enqueue → toast, the one shape every queue button shares (#144, #145 point 5).
 * Never throws: a failure is a problem toast. True when something was queued.
 */
export async function enqueueWithPreflight({
  busy,
  task,
  unit,
  request,
}: EnqueueOptions): Promise<boolean> {
  if (busy()) return false;
  busy.set(true);
  const toast = use(ToastService);
  try {
    if (!(await use(Preflight).ensureReady(task))) return false;
    const { enqueued } = await request();
    toast.success(
      enqueued === 0
        ? 'Nothing to queue'
        : `Queued ${enqueued} ${unit}${enqueued === 1 ? '' : 's'}`,
    );
    return enqueued > 0;
  } catch (error) {
    toast.problem(toApiError(error).toProblem());
    return false;
  } finally {
    busy.set(false);
  }
}
