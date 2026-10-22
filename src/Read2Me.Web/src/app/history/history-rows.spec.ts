import { describe, expect, it } from 'bun:test';
import type { HistoryStep } from '@app/api';
import { dayLabel, historyGroups, historyRow, stepTime } from './history-rows';

/** Local time, so the specs read the same in any time zone. */
const at = (day: number, hour: number, minute: number) =>
  new Date(2026, 9, day, hour, minute).toISOString();
const NOW = new Date(2026, 9, 20, 15, 0);

function step(overrides: Partial<HistoryStep> = {}): HistoryStep {
  return {
    stepId: 's1',
    parentStepId: null,
    sequence: 1,
    label: 'Edit text',
    summary: null,
    author: 'dean',
    startedAt: at(20, 14, 2),
    endedAt: at(20, 14, 2),
    isOpen: false,
    note: null,
    kind: 'request',
    action: 'edit',
    restoredFromStepId: null,
    cantOpen: false,
    ...overrides,
  };
}

describe('dayLabel', () => {
  it.each([
    [new Date(2026, 9, 20, 0, 5), 'Today'],
    [new Date(2026, 9, 19, 23, 55), 'Yesterday'],
    [new Date(2026, 9, 15, 12, 0), 'Thursday'],
    [new Date(2026, 9, 14, 12, 0), 'Wednesday'],
    [new Date(2026, 9, 13, 12, 0), 'Oct 13, 2026'],
    [new Date(2025, 11, 31, 12, 0), 'Dec 31, 2025'],
  ])('%s is %s', (date, label) => {
    expect(dayLabel(date, NOW)).toBe(label);
  });
});

describe('stepTime', () => {
  it.each<[string, Partial<HistoryStep>, string]>([
    ['a one-commit step shows its time', {}, '14:02'],
    ['a step within one minute shows one time', { endedAt: at(20, 14, 2) }, '14:02'],
    ['a run shows its span', { endedAt: at(20, 14, 20) }, '14:02–14:20'],
    [
      'an open run shows its span so far',
      { endedAt: at(20, 14, 20), isOpen: true },
      '14:02–14:20, running',
    ],
    ['an open step with one change is running', { isOpen: true }, '14:02, running'],
    ['a step from before endedAt meant last change', { endedAt: null }, '14:02'],
  ])('%s', (_, overrides, time) => {
    expect(stepTime(step(overrides))).toBe(time);
  });
});

describe('historyRow', () => {
  it('shows the label, the note once in brackets, and what changed', () => {
    const row = historyRow(
      step({
        label: 'Attribution',
        note: 'part 2 · 212 of 340 paragraphs, cancelled',
        summary: '340 paragraphs',
      }),
      null,
    );
    expect(row).toMatchObject({
      label: 'Attribution',
      note: '(part 2 · 212 of 340 paragraphs, cancelled)',
      summary: '340 paragraphs',
    });
  });

  it('has no note when nothing is unusual', () => {
    expect(historyRow(step(), null).note).toBeNull();
  });

  it.each<[string, Partial<HistoryStep>, string, string]>([
    ['attribution', { kind: 'backgroundRun', action: 'attribution' }, 'backgroundRun', 'smart_toy'],
    [
      'a voice batch',
      { kind: 'backgroundRun', action: 'voice' },
      'backgroundRun',
      'record_voice_over',
    ],
    ['a Restore', { kind: 'restore', action: null }, 'restore', 'restart_alt'],
    ['History started', { kind: 'started', action: null }, 'step', 'history'],
    ['the import', { kind: 'import', action: null }, 'step', 'upload_file'],
    ['an edit', { kind: 'request', action: 'edit' }, 'step', 'edit'],
    ['Edit with AI', { kind: 'request', action: 'editWithAi' }, 'step', 'auto_awesome'],
    ['an insert', { kind: 'request', action: 'insert' }, 'step', 'add'],
    ['a split', { kind: 'request', action: 'split' }, 'step', 'call_split'],
    ['a merge', { kind: 'request', action: 'merge' }, 'step', 'merge'],
    ['a delete', { kind: 'request', action: 'delete' }, 'step', 'delete'],
    ['a title', { kind: 'request', action: 'title' }, 'step', 'title'],
    ['a pause', { kind: 'request', action: 'pause' }, 'step', 'pause'],
    ['a character action', { kind: 'request', action: 'character' }, 'step', 'person'],
    ['a voice action', { kind: 'request', action: 'voice' }, 'step', 'record_voice_over'],
  ])('gives %s its kind and icon', (_, overrides, kind, icon) => {
    expect(historyRow(step(overrides), null)).toMatchObject({ kind, icon });
  });

  it('never reads the kind or icon from the label', () => {
    const row = historyRow(step({ label: 'Delete voice', kind: 'request', action: 'merge' }), null);

    expect(row).toMatchObject({ kind: 'step', icon: 'merge' });
  });

  it('marks the Head as current', () => {
    expect(historyRow(step(), 's1').current).toBe(true);
    expect(historyRow(step(), 's2').current).toBe(false);
  });

  it("carries a step's can't-be-opened flag", () => {
    expect(historyRow(step({ cantOpen: true }), null).cantOpen).toBe(true);
  });
});

describe('historyGroups', () => {
  it('groups newest-first steps by the day they started, keeping their order', () => {
    const steps = [
      step({ stepId: 's4', startedAt: at(20, 9, 0), endedAt: at(20, 9, 0) }),
      step({ stepId: 's3', startedAt: at(20, 8, 0), endedAt: at(20, 8, 0) }),
      step({ stepId: 's2', startedAt: at(19, 23, 50), endedAt: at(20, 0, 10) }),
      step({ stepId: 's1', startedAt: at(12, 10, 0), endedAt: at(12, 10, 0) }),
    ];

    const groups = historyGroups(steps, 's4', NOW);

    expect(groups.map((g) => [g.day, g.rows.map((r) => r.stepId)])).toEqual([
      ['Today', ['s4', 's3']],
      ['Yesterday', ['s2']],
      ['Oct 12, 2026', ['s1']],
    ]);
  });

  it('is empty before the first step', () => {
    expect(historyGroups([], null, NOW)).toEqual([]);
  });
});
