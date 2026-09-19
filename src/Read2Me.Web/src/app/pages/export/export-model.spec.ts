import { ApiError } from '@app/api';
import {
  formatBytes,
  missingAudioCount,
  partialPrompt,
  phaseSteps,
  runBanner,
} from './export-model';

describe('partial build prompt', () => {
  it('reads the missing-audio count off the 409 the host answers', () => {
    const blocked = new ApiError(409, 'Conflict', '3 items still need audio.', {
      audioRemainingCount: 3,
    });
    expect(missingAudioCount(blocked)).toBe(3);
  });

  it('is not offered for the other 409 (a run is already active) or any other failure', () => {
    expect(missingAudioCount(new ApiError(409, 'Conflict', 'Assembly is already running.'))).toBe(
      null,
    );
    expect(missingAudioCount(new ApiError(409, 'Conflict', 'x', { audioRemainingCount: 0 }))).toBe(
      null,
    );
    expect(missingAudioCount(new ApiError(500, 'Boom', 'x', { audioRemainingCount: 3 }))).toBe(
      null,
    );
    expect(missingAudioCount(new Error('network'))).toBe(null);
  });

  it('names how many items the partial build leaves out, as a destructive confirm', () => {
    expect(partialPrompt(3)).toEqual({
      title: 'Assemble a partial audiobook?',
      message:
        '3 items are missing audio and will be left out. The file is saved with _partial_ in its name.',
      confirmLabel: 'Assemble partial — 3 items missing audio',
      destructive: true,
    });
    expect(partialPrompt(1).confirmLabel).toBe('Assemble partial — 1 item missing audio');
    expect(partialPrompt(1).message).toContain('1 item is missing audio');
  });
});

describe('phaseSteps', () => {
  const states = (...args: Parameters<typeof phaseSteps>) =>
    phaseSteps(...args).map((s) => s.state);

  it('lists the five phases in run order with their labels', () => {
    const steps = phaseSteps({ isRunning: false, phase: null, outcome: null });
    expect(steps.map((s) => s.label)).toEqual([
      'Gather',
      'Silence',
      'Probe & concat',
      'Encode',
      'Finalize',
    ]);
  });

  it('is all pending before anything ran and while a run has not named a phase yet', () => {
    expect(states({ isRunning: false, phase: null, outcome: null })).toEqual(
      Array(5).fill('pending'),
    );
    expect(states({ isRunning: true, phase: null, outcome: null })).toEqual(
      Array(5).fill('pending'),
    );
  });

  it('marks earlier phases done, the current one active and the rest pending', () => {
    expect(states({ isRunning: true, phase: 'ProbeConcat', outcome: null })).toEqual([
      'done',
      'done',
      'active',
      'pending',
      'pending',
    ]);
  });

  it('is all done once the run completed', () => {
    expect(states({ isRunning: false, phase: null, outcome: 'completed' })).toEqual(
      Array(5).fill('done'),
    );
  });

  it('stops at the phase a failed or cancelled run reached', () => {
    expect(states({ isRunning: false, phase: 'Encode', outcome: 'cancelled' })).toEqual([
      'done',
      'done',
      'done',
      'cancelled',
      'pending',
    ]);
    expect(states({ isRunning: false, phase: 'Silence', outcome: 'failed' })).toEqual([
      'done',
      'failed',
      'pending',
      'pending',
      'pending',
    ]);
  });

  it('ignores a stale outcome while a new run is going', () => {
    expect(states({ isRunning: true, phase: 'Gather', outcome: 'cancelled' })).toEqual([
      'active',
      'pending',
      'pending',
      'pending',
      'pending',
    ]);
  });

  it('treats an unknown phase name as not started', () => {
    expect(states({ isRunning: true, phase: 'Mastering', outcome: null })).toEqual(
      Array(5).fill('pending'),
    );
  });
});

describe('runBanner', () => {
  const idle = { isRunning: false, outcome: null, error: null, outputFileName: null };

  it('says what the last run came to', () => {
    expect(runBanner({ ...idle, outcome: 'cancelled' })).toEqual({
      status: 'warn',
      label: 'Cancelled',
    });
    // ffmpeg's stderr opens with its version banner; what went wrong is on the last line.
    const stderr =
      'ffmpeg encode failed: 62. 28.101\r\n  libavformat 62\r\nError opening input\r\n';
    expect(runBanner({ ...idle, outcome: 'failed', error: stderr })).toEqual({
      status: 'error',
      label: 'Failed: Error opening input',
    });
    expect(runBanner({ ...idle, outcome: 'completed', outputFileName: 'Dune.m4b' })).toEqual({
      status: 'ok',
      label: 'Finished: Dune.m4b',
    });
  });

  it('shows nothing while running or before any run', () => {
    expect(runBanner({ ...idle, isRunning: true, outcome: 'failed', error: 'x' })).toBeNull();
    expect(runBanner(idle)).toBeNull();
  });
});

describe('formatBytes', () => {
  it('scales to the largest unit that keeps the number readable', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(900)).toBe('900 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(250 * 1024 * 1024)).toBe('250 MB');
    expect(formatBytes(1.25 * 1024 ** 3)).toBe('1.3 GB');
  });
});
