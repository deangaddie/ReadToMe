import { describe, expect, it } from 'bun:test';
import type { CheckpointPill, PipelineStep, StepAction, StepState } from './pipeline-steps';
import { nextStep } from './next-step';

const CONFIRM: CheckpointPill = {
  id: 'confirm',
  label: 'Confirm',
  state: 'confirmed',
};
const SKIP: CheckpointPill = { id: 'skip', label: 'Skip', state: 'skipped' };
const REOPEN: CheckpointPill = { id: 'reopen', label: 'Reopen', state: 'open' };
const RECONFIRM: CheckpointPill = {
  id: 'reconfirm',
  label: 'Reconfirm',
  state: 'confirmed',
};
const RECONFIRM_SKIP: CheckpointPill = {
  id: 'reconfirmSkip',
  label: 'Reconfirm skip',
  state: 'skipped',
};

const CHECK: StepAction = {
  id: 'checkStructure',
  label: 'Check structure',
  icon: 'menu_book',
  primary: true,
  blockedReason: null,
};

function step(overrides: Partial<PipelineStep> & { state: StepState }): PipelineStep {
  return {
    id: 'structure',
    title: 'Structure',
    icon: 'menu_book',
    chip: { status: 'neutral', label: 'Not confirmed' },
    detail: '',
    next: false,
    checkpoint: [],
    laterDone: 0,
    advice: null,
    actions: [CHECK],
    ...overrides,
  };
}

const IMPORT_DONE = step({
  id: 'import',
  title: 'Import',
  state: 'done',
  chip: { status: 'ok', label: 'Done' },
  actions: [],
});

describe('nextStep', () => {
  it('names the next step and its primary action, with its pills', () => {
    const next = nextStep([
      IMPORT_DONE,
      step({ state: 'unconfirmed', next: true, checkpoint: [CONFIRM, SKIP] }),
    ]);
    expect(next).toEqual({
      kind: 'act',
      step: 'structure',
      label: 'Next: Structure · Check structure',
      tint: 'next',
      action: 'checkStructure',
      tooltip: null,
      pills: [CONFIRM, SKIP],
    });
  });

  it('carries the primary action’s advice line as the tooltip, not the other actions’', () => {
    const titles = step({
      id: 'titles',
      title: 'Titles',
      state: 'unconfirmed',
      next: true,
      actions: [
        {
          id: 'addTitles',
          label: 'Add titles…',
          icon: 'title',
          primary: true,
          blockedReason: null,
        },
      ],
    });
    const structure = step({ state: 'unconfirmed' });
    const next = nextStep([structure, titles]);
    expect(next.kind === 'act' && next.tooltip).toBe(
      'Usually done after Structure: a reread drops inserted titles.',
    );
  });

  it('never offers Reopen', () => {
    const next = nextStep([step({ state: 'remaining', next: true, checkpoint: [REOPEN] })]);
    expect(next.kind === 'act' && next.pills).toEqual([]);
  });

  it.each([
    { pill: RECONFIRM, was: 'confirmed' },
    { pill: RECONFIRM_SKIP, was: 'skipped' },
  ])(
    'a stale step reads “changed since you $was”, still runs its action and offers $pill.label',
    (c) => {
      const next = nextStep([
        step({
          state: 'stale',
          next: true,
          chip: { status: 'warn', label: `Changed since you ${c.was}` },
          checkpoint: [c.pill, REOPEN],
        }),
      ]);
      expect(next).toEqual({
        kind: 'stale',
        step: 'structure',
        label: `Structure changed since you ${c.was}`,
        tint: 'warn',
        action: 'checkStructure',
        tooltip: null,
        pills: [c.pill],
      });
    },
  );

  it('a running step shows step · chip with a busy tint and opens the Overview', () => {
    const next = nextStep([
      step({
        id: 'speakers',
        title: 'Speakers',
        state: 'running',
        next: true,
        chip: { status: 'busy', label: '12 queued' },
        actions: [
          {
            id: 'attribute',
            label: 'Attribute unprocessed',
            icon: 'record_voice_over',
            primary: true,
            blockedReason: null,
          },
        ],
      }),
    ]);
    expect(next).toEqual({
      kind: 'waiting',
      step: 'speakers',
      label: 'Speakers · 12 queued',
      tint: 'busy',
      tooltip: null,
      pills: [],
    });
  });

  it('a blocked primary action shows step · chip, neutral, with the reason as tooltip and its pills', () => {
    const confirmReview: CheckpointPill = {
      id: 'confirm',
      label: 'Confirm review',
      state: 'confirmed',
    };
    const next = nextStep([
      step({
        id: 'speakers',
        title: 'Speakers',
        state: 'unconfirmed',
        next: true,
        chip: { status: 'neutral', label: 'Review not confirmed' },
        checkpoint: [confirmReview],
        actions: [
          {
            id: 'attribute',
            label: 'Attribute unprocessed',
            icon: 'record_voice_over',
            primary: true,
            blockedReason: 'Every paragraph has a speaker',
          },
        ],
      }),
    ]);
    expect(next).toEqual({
      kind: 'waiting',
      step: 'speakers',
      label: 'Speakers · Review not confirmed',
      tint: 'neutral',
      tooltip: 'Every paragraph has a speaker',
      pills: [confirmReview],
    });
  });

  it('reads “All steps done” when no step is next', () => {
    expect(nextStep([IMPORT_DONE])).toEqual({
      kind: 'allDone',
      label: 'All steps done',
      tint: 'ok',
    });
  });
});
