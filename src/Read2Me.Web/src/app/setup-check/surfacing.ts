import type { SetupCheckLane, SetupCheckReport, SetupFullRun } from '@app/api';
import type { ConfirmOptions } from '@app/ui/dialogs';
import type { StatusKind } from '@app/ui/partials';

/** Where the Setup check page lives, app-relative. */
export const SETUP_CHECK_PATH = 'settings/setup-check';

/** How many checks in the report failed. */
export const failedCount = (report: SetupCheckReport | null): number =>
  report?.rows.filter((r) => r.result === 'failed').length ?? 0;

/** The host started without a usable Workspace: every route shows the Setup check until a restart. */
export const noWorkspace = (report: SetupCheckReport | null): boolean =>
  report?.noWorkspace === true;

/**
 * The start banner shows while something fails, unless it was dismissed on this host start:
 * `dismissedStart` is the `startedAt` of the start it was dismissed on, so a restart shows it again.
 * Never in no-Workspace mode, where the Setup check page is the message.
 */
export const bannerVisible = (
  report: SetupCheckReport | null,
  dismissedStart: string | null,
): boolean =>
  report !== null &&
  !report.noWorkspace &&
  failedCount(report) > 0 &&
  report.startedAt !== dismissedStart;

/** "Setup check: 2 failed", the shield's label and the banner's lead. */
export const failedLabel = (failed: number): string => `Setup check: ${failed} failed`;

/**
 * The app-bar shield: a spinner with "Full check n/N" while a Full check runs, green when nothing
 * failed, red with the count, neutral before any check.
 */
export function shieldView(report: SetupCheckReport | null): {
  status: StatusKind;
  failed: number;
  label: string;
} {
  if (report?.fullRun)
    return {
      status: 'busy',
      failed: 0,
      label: `Full check ${report.fullRun.step}/${report.fullRun.total}`,
    };
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

const LANE_LABEL: Record<SetupCheckLane, string> = { local: 'Local', cpu: 'CPU', gpu: 'GPU' };

/** The progress strip: "Full check 1/9 · CPU · Starting read2me-whisper", then "· cancelling" once asked. */
export function fullRunLabel(run: SetupFullRun): string {
  const label = `Full check ${run.step}/${run.total} · ${LANE_LABEL[run.lane]} · ${run.label}`;
  return run.cancelling ? `${label} · cancelling` : label;
}

/** "about 40 seconds", "about 3 minutes": the confirm's estimate, rounded up to a whole unit. */
export function estimateText(seconds: number): string {
  if (seconds < 60) {
    const s = Math.max(1, Math.ceil(seconds));
    return `about ${s} ${s === 1 ? 'second' : 'seconds'}`;
  }
  const m = Math.ceil(seconds / 60);
  return `about ${m} ${m === 1 ? 'minute' : 'minutes'}`;
}

/** The confirm before a Full check: what it disrupts, and how long it's likely to take. */
export function fullCheckConfirm(report: SetupCheckReport | null): ConfirmOptions {
  return {
    title: 'Run the Full check?',
    message:
      'It encodes a 1 s tone to .m4b with the ffmpeg audio generation uses, then ' +
      'starts the transcription and similarity containers an active config uses, one at a time, ' +
      'to transcribe and score a sample clip. ' +
      'Then it stops any running GPU container and loads each configured TTS, voice design and LLM ' +
      'model in turn, one GPU container at a time. ' +
      'Afterwards it puts every container back as it found it, llama with the model it had loaded. ' +
      'Queues wait and new AI tasks are refused until it ends. ' +
      `It's likely to take ${estimateText(report?.fullCheckEstimateSeconds ?? 0)}.`,
    confirmLabel: 'Run Full check',
  };
}
