import type { Guid, HistoryStep, HistoryStepKind, StepAction } from '@app/api';
import type { IconName } from '@app/ui/icons';
import { plural } from '@app/ui/wording';

/** A Background run's step, a Restore's, or any other: the sheet draws the first two apart. */
export type HistoryRowKind = 'backgroundRun' | 'restore' | 'step';

/** One History sheet row, ready to render. */
export interface HistoryRow {
  stepId: Guid;
  kind: HistoryRowKind;
  icon: IconName;
  label: string;
  /** The step's note in brackets, "(part 2 · interrupted)", or null. */
  note: string | null;
  /** What changed, "4 paragraphs in Chapter 3", or null. */
  summary: string | null;
  /** "14:02", "14:02–14:20", or "14:02–14:20, running" while open. */
  time: string;
  /** The Head: the step the project is on. */
  current: boolean;
  cantOpen: boolean;
}

export interface HistoryGroup {
  /** "Today", "Yesterday", a weekday within the week, else the date. */
  day: string;
  rows: HistoryRow[];
}

/**
 * The icon of a request's or Background run's step, by its action. Keyed by the host's
 * `StepAction`, so a new action fails the typecheck until it has an icon.
 */
const ACTION_ICONS: Record<StepAction, IconName> = {
  Edit: 'edit',
  EditWithAi: 'auto_awesome',
  Insert: 'add',
  Split: 'call_split',
  Merge: 'merge',
  Delete: 'delete',
  Title: 'title',
  Pause: 'pause',
  Character: 'person',
  Voice: 'record_voice_over',
  Attribution: 'smart_toy',
  Pronunciation: 'text_to_speech',
};

/** The icon of a step with no action, by its kind. */
const KIND_ICONS: Record<HistoryStepKind, IconName> = {
  Request: 'edit',
  BackgroundRun: 'smart_toy',
  Restore: 'restart_alt',
  Started: 'history',
  Import: 'upload_file',
};

export function historyGroups(
  steps: readonly HistoryStep[],
  headStepId: Guid | null,
  now: Date,
): HistoryGroup[] {
  const groups: HistoryGroup[] = [];
  for (const step of steps) {
    const day = dayLabel(new Date(step.startedAt), now);
    const row = historyRow(step, headStepId);
    const last = groups.at(-1);
    if (last?.day === day) last.rows.push(row);
    else groups.push({ day, rows: [row] });
  }
  return groups;
}

export function historyRow(step: HistoryStep, headStepId: Guid | null): HistoryRow {
  const kind: HistoryRowKind =
    step.kind === 'Restore' ? 'restore' : step.kind === 'BackgroundRun' ? 'backgroundRun' : 'step';
  return {
    stepId: step.stepId,
    kind,
    // From the step's kind and action, never its label: renaming a label changes no icon.
    icon: step.action === null ? KIND_ICONS[step.kind] : ACTION_ICONS[step.action],
    label: step.label,
    // Notes come without brackets; the sheet adds them, once: "Attribution (part 2 · interrupted)".
    note: step.note === null ? null : `(${step.note})`,
    summary: step.summary,
    time: stepTime(step),
    current: step.stepId === headStepId,
    cantOpen: step.cantOpen,
  };
}

/** The step's time, or its span when it ran past the minute it started in; an open step says it is running. */
export function stepTime(step: HistoryStep): string {
  const start = clock(new Date(step.startedAt));
  const end = step.endedAt === null ? start : clock(new Date(step.endedAt));
  const span = end === start ? start : `${start}–${end}`;
  return step.isOpen ? `${span}, running` : span;
}

/** What a Restore to the viewed step costs, said before the click; "" until the preview has read. */
export function restoreCost(audioDiscardCount: number | null): string {
  if (audioDiscardCount === null) return '';
  if (audioDiscardCount === 0) return 'Restoring discards no audio.';
  return `Restoring discards audio on ${audioDiscardCount} item${plural(audioDiscardCount)}.`;
}

export function dayLabel(date: Date, now: Date): string {
  const days = Math.round((midnight(now) - midnight(date)) / 86_400_000);
  if (days === 0) return 'Today';
  if (days === 1) return 'Yesterday';
  if (days > 1 && days < 7) return date.toLocaleDateString('en-US', { weekday: 'long' });
  return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

function midnight(date: Date): number {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
}

function clock(date: Date): string {
  return date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
}
