import { ApiError, AssemblyPhase } from '@app/api';
import { ConfirmOptions } from '@app/ui/confirm-dialog/confirm-dialog';
import { StatusKind } from '@app/ui/status-chip/status-chip';

/**
 * The export page's rules (ticket 20), kept pure so they are table-tested and the component only
 * renders: when a partial build is offered, how hub assembly state maps onto the phase stepper,
 * and what the last run came to.
 */

/** How a run ended, from the hub's terminal `assembly` kinds. */
export type RunOutcome = 'completed' | 'failed' | 'cancelled';

export type PhaseStepState = 'pending' | 'active' | 'done' | 'failed' | 'cancelled';

export interface PhaseStep {
  phase: AssemblyPhase;
  label: string;
  state: PhaseStepState;
}

/** `AssemblyPhase` in run order (research/live-events.md §1.4). */
const PHASES: readonly { phase: AssemblyPhase; label: string }[] = [
  { phase: 'Gather', label: 'Gather' },
  { phase: 'Silence', label: 'Silence' },
  { phase: 'ProbeConcat', label: 'Probe & concat' },
  { phase: 'Encode', label: 'Encode' },
  { phase: 'Finalize', label: 'Finalize' },
];

export interface PhaseInput {
  isRunning: boolean;
  /** The current phase while running; afterwards the phase the run had reached. */
  phase: string | null | undefined;
  /** Only read once the run is over, so a stale one cannot colour a new run. */
  outcome: RunOutcome | null;
}

export function phaseSteps({ isRunning, phase, outcome }: PhaseInput): PhaseStep[] {
  if (!isRunning && outcome === 'completed') return PHASES.map((p) => ({ ...p, state: 'done' }));

  const reachedIndex = PHASES.findIndex((p) => p.phase === phase);
  const stopped: PhaseStepState | null =
    !isRunning && (outcome === 'failed' || outcome === 'cancelled') ? outcome : null;
  if (reachedIndex < 0 || (!isRunning && !stopped))
    return PHASES.map((p) => ({ ...p, state: 'pending' }));

  return PHASES.map((p, i) => ({
    ...p,
    state: i < reachedIndex ? 'done' : i > reachedIndex ? 'pending' : (stopped ?? 'active'),
  }));
}

export interface BannerInput {
  isRunning: boolean;
  outcome: RunOutcome | null;
  error: string | null | undefined;
  outputFileName: string | null | undefined;
}

/** One line for how the last run ended; null while running or before any run. */
export function runBanner(input: BannerInput): { status: StatusKind; label: string } | null {
  if (input.isRunning) return null;
  switch (input.outcome) {
    case 'completed':
      return {
        status: 'ok',
        label: input.outputFileName ? `Finished: ${input.outputFileName}` : 'Finished',
      };
    case 'cancelled':
      return { status: 'warn', label: 'Cancelled' };
    case 'failed': {
      // ffmpeg's stderr opens with its version banner; the line naming the failure is the last.
      const reason = input.error
        ?.split('\n')
        .map((line) => line.trim())
        .filter(Boolean)
        .at(-1);
      return { status: 'error', label: reason ? `Failed: ${reason}` : 'Failed' };
    }
    default:
      return null;
  }
}

/**
 * The host refuses a full build with 409 + `audioRemainingCount` while items lack audio; that is
 * the cue to offer a partial one. Its other 409 (a run is already active) carries no count.
 */
export function missingAudioCount(error: unknown): number | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null;
  const count = error.extensions['audioRemainingCount'];
  return typeof count === 'number' && count > 0 ? count : null;
}

export function partialPrompt(missing: number): ConfirmOptions {
  const items = missing === 1 ? '1 item' : `${missing} items`;
  return {
    title: 'Assemble a partial audiobook?',
    message: `${items} ${missing === 1 ? 'is' : 'are'} missing audio and will be left out. The file is saved with _partial_ in its name.`,
    confirmLabel: `Assemble partial — ${items} missing audio`,
    destructive: true,
  };
}

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const;

export function formatBytes(bytes: number): string {
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  // One decimal only where it says something: 1.5 KB, but 250 MB and 900 B.
  const text = unit === 0 || value >= 100 ? Math.round(value).toString() : value.toFixed(1);
  return `${text.replace(/\.0$/, '')} ${UNITS[unit]}`;
}
