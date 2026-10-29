import type { SetupCheckReport } from '@app/api';
import type { StatusKind } from '@app/ui/partials';

/** Where the Setup check page lives, app-relative. */
export const SETUP_CHECK_PATH = 'settings/setup-check';

/** How many checks in the report failed. */
export const failedCount = (report: SetupCheckReport | null): number =>
  report?.rows.filter((r) => r.result === 'failed').length ?? 0;

/**
 * The start banner shows while something fails, unless it was dismissed on this host start:
 * `dismissedStart` is the `startedAt` of the start it was dismissed on, so a restart shows it again.
 */
export const bannerVisible = (
  report: SetupCheckReport | null,
  dismissedStart: string | null,
): boolean => report !== null && failedCount(report) > 0 && report.startedAt !== dismissedStart;

/** "Setup check: 2 failed", the shield's label and the banner's lead. */
export const failedLabel = (failed: number): string => `Setup check: ${failed} failed`;

/** The app-bar shield: green when nothing failed, red with the count, neutral before any check. */
export function shieldView(report: SetupCheckReport | null): {
  status: StatusKind;
  failed: number;
  label: string;
} {
  if (!report?.quickCheckedAt)
    return { status: 'neutral', failed: 0, label: 'Setup check not run yet' };
  const failed = failedCount(report);
  return failed > 0
    ? { status: 'error', failed, label: failedLabel(failed) }
    : { status: 'ok', failed: 0, label: 'Setup check: nothing failed' };
}

/** Where `/` lands: the Setup check on the start that created app.db, Projects otherwise. */
export const landingPath = (report: SetupCheckReport | null): string =>
  report?.firstRun ? SETUP_CHECK_PATH : 'projects';
