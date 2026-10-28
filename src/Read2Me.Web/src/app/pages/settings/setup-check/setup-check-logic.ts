import type { SetupCheckGroup, SetupCheckReport, SetupCheckResult, SetupCheckRow } from '@app/api';
import type { IconName } from '@app/ui/icons';
import type { StatusKind } from '@app/ui/partials';

/** How each result renders as a `statusChip`. */
export const RESULT_VIEW: Record<
  SetupCheckResult,
  { status: StatusKind; label: string; icon?: IconName }
> = {
  passed: { status: 'ok', label: 'Passed' },
  failed: { status: 'error', label: 'Failed' },
  notConfirmed: { status: 'warn', label: 'Not confirmed', icon: 'question_mark' },
  notRun: { status: 'neutral', label: 'Not run', icon: 'block' },
  notUsed: { status: 'neutral', label: 'Not used' },
  checking: { status: 'busy', label: 'Checking…' },
};

/** The table's groups, in order. */
export const GROUPS: readonly { group: SetupCheckGroup; label: string }[] = [
  { group: 'workspaceAndTools', label: 'Workspace and tools' },
  { group: 'aiConfigs', label: 'AI configs' },
  { group: 'dockerServices', label: 'Docker services' },
];

/** The summary's counts, in this order; a result nothing has is left out. */
const SUMMARY_ORDER: readonly SetupCheckResult[] = [
  'failed',
  'passed',
  'notConfirmed',
  'notRun',
  'notUsed',
  'checking',
];

/** The local wall-clock time of an ISO instant, `HH:mm`. */
export function clockTime(iso: string): string {
  const at = new Date(iso);
  const two = (n: number) => String(n).padStart(2, '0');
  return `${two(at.getHours())}:${two(at.getMinutes())}`;
}

/** "1 failed · 2 passed — Quick check 14:02 · Full check not run yet". */
export function summaryLine(
  report: SetupCheckReport,
  time: (iso: string) => string = clockTime,
): string {
  const counts = SUMMARY_ORDER.map((result) => ({
    result,
    n: report.rows.filter((r) => r.result === result).length,
  }))
    .filter((c) => c.n > 0)
    .map((c) => `${c.n} ${RESULT_VIEW[c.result].label.toLowerCase()}`);
  const quick = report.quickCheckedAt
    ? `Quick check ${time(report.quickCheckedAt)}`
    : 'Quick check not run yet';
  const full = report.fullCheckedAt
    ? `Full check ${time(report.fullCheckedAt)}`
    : 'Full check not run yet';
  const stamp = `${quick} · ${full}`;
  return counts.length > 0 ? `${counts.join(' · ')} — ${stamp}` : stamp;
}

/** The Fix cell shows the hint on every row but Passed and Not used, which show "—". */
export const showsFix = (row: SetupCheckRow): boolean =>
  row.result !== 'passed' && row.result !== 'notUsed';

/** The groups that hold rows, each with its rows in report order. */
export function groupedRows(
  rows: readonly SetupCheckRow[],
): { label: string; rows: SetupCheckRow[] }[] {
  return GROUPS.map((g) => ({
    label: g.label,
    rows: rows.filter((r) => r.group === g.group),
  })).filter((g) => g.rows.length > 0);
}
