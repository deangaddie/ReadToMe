import { describe, expect, it } from 'bun:test';
import type { SetupCheckReport, SetupCheckResult, SetupCheckRow } from '@app/api';
import { clockTime, groupedRows, showsFix, summaryLine } from './setup-check-logic';

const row = (
  id: string,
  result: SetupCheckResult,
  group: SetupCheckRow['group'] = 'workspaceAndTools',
): SetupCheckRow => ({
  id,
  group,
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
  rows: SetupCheckRow[],
  quickCheckedAt: string | null,
  fullCheckedAt: string | null = null,
): SetupCheckReport => ({
  rows,
  quickCheckedAt,
  fullCheckedAt,
  firstRun: false,
  startedAt: '2026-10-28T14:00:00Z',
  noWorkspace: false,
});

/** A fixed formatter, so the line doesn't depend on the machine's time zone. */
const at = (iso: string) => (iso.includes('T14') ? '14:02' : '15:30');

describe('summaryLine', () => {
  it('counts failures first, leaves out results nothing has, then the check times', () => {
    const line = summaryLine(
      report([row('a', 'passed'), row('b', 'failed'), row('c', 'passed')], '2026-10-28T14:02:00Z'),
      at,
    );
    expect(line).toBe('1 failed · 2 passed — Quick check 14:02 · Full check not run yet');
  });

  it('names every result kind it holds, in order', () => {
    const line = summaryLine(
      report(
        [row('a', 'notUsed'), row('b', 'notRun'), row('c', 'notConfirmed'), row('d', 'passed')],
        '2026-10-28T14:02:00Z',
        '2026-10-28T15:30:00Z',
      ),
      at,
    );
    expect(line).toBe(
      '1 passed · 1 not confirmed · 1 not run · 1 not used — Quick check 14:02 · Full check 15:30',
    );
  });

  it('says neither check has run on an empty report', () => {
    expect(summaryLine(report([], null), at)).toBe(
      'Quick check not run yet · Full check not run yet',
    );
  });
});

describe('clockTime', () => {
  it('is the local HH:mm', () => {
    const local = new Date(2026, 9, 28, 9, 5).toISOString();
    expect(clockTime(local)).toBe('09:05');
  });
});

describe('showsFix', () => {
  it.each([
    ['passed', false],
    ['notUsed', false],
    ['failed', true],
    ['notConfirmed', true],
    ['notRun', true],
  ] as const)('%s → %p', (result, shown) => {
    expect(showsFix(row('x', result))).toBe(shown);
  });
});

describe('groupedRows', () => {
  it('keeps the table order of groups and drops empty ones', () => {
    const groups = groupedRows([
      row('svc', 'passed', 'dockerServices'),
      row('ws', 'passed'),
      row('ff', 'failed'),
    ]);
    expect(groups.map((g) => [g.label, g.rows.map((r) => r.id)])).toEqual([
      ['Workspace and tools', ['ws', 'ff']],
      ['Docker services', ['svc']],
    ]);
  });
});
