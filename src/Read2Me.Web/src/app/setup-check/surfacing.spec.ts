import { describe, expect, it } from 'bun:test';
import type { SetupCheckReport, SetupCheckResult, SetupCheckRow } from '@app/api';
import { bannerVisible, landingPath, shieldView } from './surfacing';

const STARTED = '2026-10-29T09:00:00Z';
const RESTARTED = '2026-10-29T11:30:00Z';

const row = (id: string, result: SetupCheckResult): SetupCheckRow => ({
  id,
  group: 'workspaceAndTools',
  name: id,
  tier: 'quick',
  result,
  expected: 'e',
  found: 'f',
  note: null,
  fix: { text: 'Fix it.', link: null, copy: null },
  managed: false,
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
  ...over,
});

describe('bannerVisible', () => {
  it('shows on a start whose check found a failure', () => {
    expect(bannerVisible(report(['passed', 'failed']), null)).toBe(true);
  });

  it('stays hidden when nothing failed, and before the report has loaded', () => {
    expect(bannerVisible(report(['passed', 'notConfirmed', 'notUsed']), null)).toBe(false);
    expect(bannerVisible(null, null)).toBe(false);
  });

  it('stays hidden for the rest of the start it was dismissed on', () => {
    expect(bannerVisible(report(['failed']), STARTED)).toBe(false);
  });

  it('comes back on the next start, even if the failure is the same', () => {
    expect(bannerVisible(report(['failed'], { startedAt: RESTARTED }), STARTED)).toBe(true);
  });
});

describe('shieldView', () => {
  it('is green when nothing failed', () => {
    expect(shieldView(report(['passed', 'notConfirmed']))).toEqual({
      status: 'ok',
      failed: 0,
      label: 'Setup check: nothing failed',
    });
  });

  it('is red with the failed count otherwise', () => {
    expect(shieldView(report(['failed', 'passed', 'failed']))).toEqual({
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
});

describe('landingPath', () => {
  it('opens the Setup check on the first run', () => {
    expect(landingPath(report(['passed'], { firstRun: true }))).toBe('settings/setup-check');
  });

  it('opens Projects on every later start, and when the report could not be read', () => {
    expect(landingPath(report(['failed']))).toBe('projects');
    expect(landingPath(null)).toBe('projects');
  });
});
