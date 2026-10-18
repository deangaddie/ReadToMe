import type { StatusKind } from '@app/ui/partials';
import {
  type CheckpointPill,
  type PipelineStep,
  type StepActionId,
  type StepId,
  adviceFor,
} from './pipeline-steps';

/** The pill's colour: the accent for a plain next step, else the state's status colour. */
export type NextStepTint = 'next' | Extract<StatusKind, 'warn' | 'busy' | 'neutral' | 'ok'>;

/**
 * What the app bar's next-step pill says (guidance-primitives spec §4), from the derived Pipeline.
 * `act` and `stale` run the step's primary action; `waiting` (running, or the primary action
 * blocked) only opens the Overview, with the blocked reason as its tooltip. Every step state
 * carries its checkpoint pills, never Reopen; `allDone` has no step.
 */
export type NextStep =
  | {
      kind: 'act' | 'stale';
      step: StepId;
      label: string;
      tint: NextStepTint;
      action: StepActionId;
      /** The primary action's advice line, or null. */
      tooltip: string | null;
      pills: CheckpointPill[];
    }
  | {
      kind: 'waiting';
      step: StepId;
      label: string;
      tint: NextStepTint;
      /** The primary action's blocked reason, or null while it runs. */
      tooltip: string | null;
      pills: CheckpointPill[];
    }
  | { kind: 'allDone'; label: string; tint: NextStepTint };

export function nextStep(steps: readonly PipelineStep[]): NextStep {
  const step = steps.find((s) => s.next);
  if (!step) return { kind: 'allDone', label: 'All steps done', tint: 'ok' };
  const pills = step.checkpoint.filter((p) => p.id !== 'reopen');
  const primary = step.actions.find((a) => a.primary);
  if (step.state === 'running' || !primary || primary.blockedReason !== null) {
    return {
      kind: 'waiting',
      step: step.id,
      label: `${step.title} · ${step.chip.label}`,
      tint: step.state === 'running' ? 'busy' : 'neutral',
      tooltip: primary?.blockedReason ?? null,
      pills,
    };
  }
  const base = { step: step.id, action: primary.id, tooltip: adviceFor(primary.id, steps), pills };
  if (step.state !== 'stale')
    return { kind: 'act', label: `Next: ${step.title} · ${primary.label}`, tint: 'next', ...base };
  // A stale step offers the Reconfirm that matches its row: Reconfirm skip only after a skip.
  const was = pills.some((p) => p.id === 'reconfirmSkip') ? 'skipped' : 'confirmed';
  return { kind: 'stale', label: `${step.title} changed since you ${was}`, tint: 'warn', ...base };
}
