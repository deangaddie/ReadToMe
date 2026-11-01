import { describe, expect, it } from 'bun:test';
import type { SetupCheckReport, SetupCheckResult, SetupCheckRow } from '@app/api';
import {
  clockTime,
  groupedRows,
  rerunBlockedReason,
  showsFix,
  summaryLine,
} from './setup-check-logic';

const row = (
  id: string,
  result: SetupCheckResult,
  group: SetupCheckRow['group'] = 'WorkspaceAndTools',
): SetupCheckRow => ({
  id,
  group,
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
  fullRun: null,
  fullCheckEstimateSeconds: 3,
  putBack: null,
});

/** A fixed formatter, so the line doesn't depend on the machine's time zone. */
const at = (iso: string) => (iso.includes('T14') ? '14:02' : '15:30');

describe('summaryLine', () => {
  it('counts failures first, leaves out results nothing has, then the check times', () => {
    const line = summaryLine(
      report([row('a', 'Passed'), row('b', 'Failed'), row('c', 'Passed')], '2026-10-28T14:02:00Z'),
      at,
    );
    expect(line).toBe('1 failed · 2 passed — Quick check 14:02 · Full check not run yet');
  });

  it('names every result kind it holds, in order', () => {
    const line = summaryLine(
      report(
        [row('a', 'NotUsed'), row('b', 'NotRun'), row('c', 'NotConfirmed'), row('d', 'Passed')],
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
    ['Passed', false],
    ['NotUsed', false],
    ['Failed', true],
    ['NotConfirmed', true],
    ['NotRun', true],
  ] as const)('%s → %p', (result, shown) => {
    expect(showsFix(row('x', result))).toBe(shown);
  });
});

describe('groupedRows', () => {
  it('keeps the table order of groups and drops empty ones', () => {
    const groups = groupedRows([
      row('svc', 'Passed', 'DockerServices'),
      row('ws', 'Passed'),
      row('ff', 'Failed'),
    ]);
    expect(groups.map((g) => [g.label, g.rows.map((r) => r.id)])).toEqual([
      ['Workspace and tools', ['ws', 'ff']],
      ['Docker services', ['svc']],
    ]);
  });
});

describe('rerunBlockedReason', () => {
  const full: SetupCheckRow = { ...row('llm', 'Failed'), tier: 'Full', rerun: { gpu: true } };
  const run = { step: 1, total: 1, lane: 'Gpu' as const, label: 'Loading', cancelling: false };
  const cases: [string, SetupCheckRow, Partial<SetupCheckReport>, RegExp | null][] = [
    ['a row with something to send, nothing running', full, {}, null],
    ['no Workspace', full, { noWorkspace: true }, /Workspace/],
    ['a run going', full, { fullRun: run }, /check is running/],
    ['no active config', { ...full, rerun: null }, {}, /no active config/],
  ];
  for (const [name, r, over, expected] of cases)
    it(name, () => {
      const reason = rerunBlockedReason(r, { ...report([r], '2026-10-28T14:02:00Z'), ...over });
      if (expected === null) expect(reason).toBeNull();
      else expect(reason).toMatch(expected);
    });
});
