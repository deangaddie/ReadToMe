import type { Guid, HistoryStep } from '@app/api';
import type { IconName } from '@app/ui/icons';

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
 * The labels the host gives a Background run's step (`BackgroundRuns.LabelOf`). The wire has no
 * kind field, so a Background run is told by its label; `history-rows.spec` pins them to the host.
 */
export const BACKGROUND_RUN_ICONS: Record<string, IconName> = {
  Attribution: 'smart_toy',
  'Voice batch': 'record_voice_over',
  'Attribution and voice batch': 'smart_toy',
};

/** The kind of action a label names, first match wins; the labels are the host's (`CommandStepLabels`, the endpoints). */
const LABEL_ICONS: [RegExp, IconName][] = [
  [/^History started$/, 'history'],
  [/^Import/, 'upload_file'],
  [/^Edit with AI/, 'auto_awesome'],
  [/^(Split|Re-split)/, 'call_split'],
  [/^Merge/, 'merge'],
  [/^(Delete|Remove|Clear)/, 'delete'],
  [/voice|transcri/i, 'record_voice_over'],
  [/character|speaker|narrat|alias/i, 'person'],
  [/title/i, 'title'],
  [/pause/i, 'pause'],
];

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
  const backgroundRun = BACKGROUND_RUN_ICONS[step.label];
  const kind: HistoryRowKind =
    step.restoredFromStepId !== null
      ? 'restore'
      : backgroundRun !== undefined
        ? 'backgroundRun'
        : 'step';
  const icon =
    kind === 'restore'
      ? 'restart_alt'
      : (backgroundRun ?? LABEL_ICONS.find(([pattern]) => pattern.test(step.label))?.[1] ?? 'edit');
  return {
    stepId: step.stepId,
    kind,
    icon,
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
