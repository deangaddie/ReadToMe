import { describe, expect, it } from 'bun:test';
import type { SetupCheckReport, SetupCheckResult, SetupCheckRow } from '@app/api';
import {
  bannerVisible,
  estimateText,
  fullCheckConfirm,
  fullRunLabel,
  landingPath,
  noWorkspace,
  rerunConfirm,
  shieldView,
} from './surfacing';

const STARTED = '2026-10-29T09:00:00Z';
const RESTARTED = '2026-10-29T11:30:00Z';

const row = (id: string, result: SetupCheckResult): SetupCheckRow => ({
  id,
  group: 'WorkspaceAndTools',
  name: id,
  tier: 'Quick',
  result,
  expected: 'e',
  found: 'f',
  note: null,
  fix: { text: 'Fix it.', link: null, copy: null },
  managed: false,
  rerun: null,
});

const report = (
  results: SetupCheckResult[],
  over: Partial<SetupCheckReport> = {},
): SetupCheckReport => ({
  rows: results.map((r, i) => row(`check-${i}`, r)),
  quickCheckedAt: '2026-10-29T09:00:05Z',
  fullCheckedAt: null,
  firstRun: false,
  startedAt: STARTED,
  noWorkspace: false,
  fullRun: null,
  fullCheckEstimateSeconds: 3,
  putBack: null,
  ...over,
});

describe('bannerVisible', () => {
  it('shows on a start whose check found a failure', () => {
    expect(bannerVisible(report(['Passed', 'Failed']), null)).toBe(true);
  });

  it('stays hidden when nothing failed, and before the report has loaded', () => {
    expect(bannerVisible(report(['Passed', 'NotConfirmed', 'NotUsed']), null)).toBe(false);
    expect(bannerVisible(null, null)).toBe(false);
  });

  it('stays hidden for the rest of the start it was dismissed on', () => {
    expect(bannerVisible(report(['Failed']), STARTED)).toBe(false);
  });

  it('comes back on the next start, even if the failure is the same', () => {
    expect(bannerVisible(report(['Failed'], { startedAt: RESTARTED }), STARTED)).toBe(true);
  });

  it('never shows in no-Workspace mode, where the Setup check page is the message', () => {
    expect(bannerVisible(report(['Failed', 'NotRun'], { noWorkspace: true }), null)).toBe(false);
  });
});

describe('noWorkspace', () => {
  it('follows the report, and is off before it has loaded', () => {
    expect(noWorkspace(report(['Failed'], { noWorkspace: true }))).toBe(true);
    expect(noWorkspace(report(['Failed']))).toBe(false);
    expect(noWorkspace(null)).toBe(false);
  });
});

describe('shieldView', () => {
  it('is green when nothing failed', () => {
    expect(shieldView(report(['Passed', 'NotConfirmed']))).toEqual({
      status: 'ok',
      failed: 0,
      label: 'Setup check: nothing failed',
    });
  });

  it('is red with the failed count otherwise', () => {
    expect(shieldView(report(['Failed', 'Passed', 'Failed']))).toEqual({
      status: 'error',
      failed: 2,
      label: 'Setup check: 2 failed',
    });
  });

  it('is neutral until the first Quick check has run', () => {
    const view = { status: 'neutral' as const, failed: 0, label: 'Setup check not run yet' };
    expect(shieldView(null)).toEqual(view);
    expect(shieldView(report([], { quickCheckedAt: null }))).toEqual(view);
  });

  it('turns to a spinner with the step while a Full check runs, whatever failed', () => {
    const running = report(['Failed'], {
      fullRun: {
        step: 2,
        total: 9,
        lane: 'Cpu',
        label: 'Transcribing the sample clip',
        cancelling: false,
      },
    });
    expect(shieldView(running)).toEqual({ status: 'busy', failed: 0, label: 'Full check 2/9' });
  });
});

describe('landingPath', () => {
  it('opens the Setup check on the first run', () => {
    expect(landingPath(report(['Passed'], { firstRun: true }))).toBe('settings/setup-check');
  });

  it('opens Projects on every later start, and when the report could not be read', () => {
    expect(landingPath(report(['Failed']))).toBe('projects');
    expect(landingPath(null)).toBe('projects');
  });
});

describe('fullRunLabel', () => {
  it('reads step, lane and what the run is doing, and says when it is cancelling', () => {
    const run = {
      step: 4,
      total: 9,
      lane: 'Cpu' as const,
      label: 'Starting read2me-minilm-l6 and scoring',
      cancelling: false,
    };
    expect(fullRunLabel(run)).toBe('Full check 4/9 · CPU · Starting read2me-minilm-l6 and scoring');
    expect(
      fullRunLabel({
        ...run,
        lane: 'Local',
        label: 'Encoding a 1 s tone to .m4b',
        step: 1,
        total: 1,
      }),
    ).toBe('Full check 1/1 · Local · Encoding a 1 s tone to .m4b');
    expect(fullRunLabel({ ...run, lane: 'Gpu', cancelling: true })).toBe(
      'Full check 4/9 · GPU · Starting read2me-minilm-l6 and scoring · cancelling',
    );
  });
});

describe('the Full check confirm', () => {
  it('rounds the estimate up to whole seconds, then whole minutes', () => {
    expect(estimateText(1)).toBe('about 1 second');
    expect(estimateText(3)).toBe('about 3 seconds');
    expect(estimateText(59)).toBe('about 59 seconds');
    expect(estimateText(61)).toBe('about 2 minutes');
    expect(estimateText(60)).toBe('about 1 minute');
  });

  it('says what it disrupts and how long it is likely to take', () => {
    const options = fullCheckConfirm(report(['Passed'], { fullCheckEstimateSeconds: 200 }));
    expect(options.title).toBe('Run the Full check?');
    expect(options.confirmLabel).toBe('Run Full check');
    expect(options.message).toContain(
      'encodes a 1 s tone to .m4b with the ffmpeg audio generation uses',
    );
    expect(options.message).toContain(
      'starts the transcription and similarity containers an active config uses, one at a time, to transcribe and score a sample clip.',
    );
    expect(options.message).toContain(
      'stops any running GPU container and loads each configured TTS, voice design and LLM model in turn, one GPU container at a time.',
    );
    expect(options.message).toContain(
      'puts every container back as it found it, llama with the model it had loaded.',
    );
    expect(options.message).toContain('Queues wait and new AI tasks are refused until it ends.');
    expect(options.message).toEndWith("It's likely to take about 4 minutes.");
  });
});

describe('rerunConfirm', () => {
  it('confirms only a re-run that drives a GPU container', () => {
    const gpu = {
      ...row('llm', 'Failed'),
      name: 'LLM',
      tier: 'Full' as const,
      rerun: { gpu: true },
    };
    const cpu = { ...row('transcription', 'Failed'), tier: 'Full' as const, rerun: { gpu: false } };

    expect(rerunConfirm(gpu)?.title).toBe('Re-run the LLM check?');
    expect(rerunConfirm(gpu)?.message).toContain('stops any running GPU container');
    expect(rerunConfirm(cpu)).toBeNull();
    expect(rerunConfirm(row('docker', 'Failed'))).toBeNull();
  });
});
