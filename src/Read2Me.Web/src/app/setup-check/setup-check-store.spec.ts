import { beforeEach, describe, expect, it, mock } from 'bun:test';
import { ApiError, type SetupCheckReport, SetupCheckApi, type SetupFullRun } from '@app/api';
import { override, resetServices, use } from '@app/core/services';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeLive } from '../../testing/fake-live';
import { SetupCheckStore } from './setup-check-store';

const RUN: SetupFullRun = {
  step: 1,
  total: 1,
  lane: 'local',
  label: 'Encoding a 1 s tone to .m4b',
  cancelling: false,
};

function report(
  ffmpeg: SetupCheckReport['rows'][number]['result'],
  over: Partial<SetupCheckReport> = {},
): SetupCheckReport {
  return {
    rows: [
      {
        id: 'ffmpeg',
        group: 'workspaceAndTools',
        name: 'ffmpeg',
        tier: 'full',
        result: ffmpeg,
        expected: 'e',
        found: 'f',
        note: null,
        fix: { text: 'Fix it.', link: null, copy: null },
        managed: false,
      },
    ],
    quickCheckedAt: '2026-10-29T09:00:05Z',
    fullCheckedAt: null,
    firstRun: false,
    startedAt: '2026-10-29T09:00:00Z',
    noWorkspace: false,
    fullRun: null,
    fullCheckEstimateSeconds: 3,
    putBack: null,
    ...over,
  };
}

describe('SetupCheckStore and the Full check', () => {
  let live: FakeLive;
  let api: {
    report: ReturnType<typeof mock>;
    runQuick: ReturnType<typeof mock>;
    startFull: ReturnType<typeof mock>;
    cancelFull: ReturnType<typeof mock>;
  };
  let confirmed: boolean;
  let confirms: unknown[];
  let problems: unknown[];

  const store = (): SetupCheckStore => use(SetupCheckStore);

  beforeEach(() => {
    resetServices();
    live = new FakeLive().install();
    api = {
      report: mock(async () => report('passed')),
      runQuick: mock(async () => report('passed')),
      startFull: mock(async () => report('passed', { fullRun: RUN })),
      cancelFull: mock(async () => report('checking', { fullRun: { ...RUN, cancelling: true } })),
    };
    confirmed = true;
    confirms = [];
    problems = [];
    override(SetupCheckApi, api as unknown as SetupCheckApi);
    override(ConfirmService, {
      confirm: async (options: unknown) => {
        confirms.push(options);
        return confirmed;
      },
    } as unknown as ConfirmService);
    override(ToastService, {
      problem: (p: unknown) => problems.push(p),
    } as unknown as ToastService);
  });

  it('applies hub progress to the rows, the strip and the shield, and done clears the run', async () => {
    await store().load();

    live.emit('setupCheck', { kind: 'progress', report: report('checking', { fullRun: RUN }) });

    expect(store().report()?.rows[0]?.result).toBe('checking');
    expect(store().fullRun()).toEqual(RUN);
    expect(store().shield()).toEqual({ status: 'busy', failed: 0, label: 'Full check 1/1' });

    live.emit('setupCheck', {
      kind: 'done',
      report: report('failed', { fullCheckedAt: '2026-10-29T09:05:00Z' }),
    });

    expect(store().fullRun()).toBeNull();
    expect(store().report()?.rows[0]?.result).toBe('failed');
    expect(store().shield()).toEqual({
      status: 'error',
      failed: 1,
      label: 'Setup check: 1 failed',
    });
  });

  it('reads the report again when the hub reconnects, so a missed done is not left spinning', async () => {
    store();
    live.emit('setupCheck', { kind: 'progress', report: report('checking', { fullRun: RUN }) });
    expect(store().fullRun()).toEqual(RUN);

    live.resync();
    await Bun.sleep(0);

    expect(api.report).toHaveBeenCalled();
    expect(store().fullRun()).toBeNull();
  });

  it('starts the Full check after the confirm with the estimate', async () => {
    await store().load();

    await store().runFull();

    expect(confirms).toHaveLength(1);
    expect((confirms[0] as { message: string }).message).toContain('about 3 seconds');
    expect(api.startFull).toHaveBeenCalledTimes(1);
    expect(store().fullRun()).toEqual(RUN);
  });

  it('keeps the hub done message when it arrives before the 202 that started the run', async () => {
    await store().load();
    api.startFull.mockImplementation(async () => {
      live.emit('setupCheck', { kind: 'done', report: report('failed') });
      return report('checking', { fullRun: RUN });
    });

    await store().runFull();

    expect(store().fullRun()).toBeNull();
    expect(store().report()?.rows[0]?.result).toBe('failed');
  });

  it('starts nothing when the confirm is declined', async () => {
    confirmed = false;

    await store().runFull();

    expect(api.startFull).not.toHaveBeenCalled();
  });

  it('toasts the host refusal, naming the work in flight', async () => {
    const refusal = new ApiError(
      409,
      'Work is running',
      'The Full check can’t start: the Audio queue holds 3 items.',
    );
    api.startFull.mockImplementation(async () => {
      throw refusal;
    });

    await store().runFull();

    expect(problems).toHaveLength(1);
    expect(store().fullRun()).toBeNull();
  });

  it('runs no Quick check while a Full check runs, and cancel shows it cancelling', async () => {
    store();
    live.emit('setupCheck', { kind: 'progress', report: report('checking', { fullRun: RUN }) });

    await store().runQuick();
    expect(api.runQuick).not.toHaveBeenCalled();

    await store().cancelFull();
    expect(store().fullRun()?.cancelling).toBe(true);
  });
});
