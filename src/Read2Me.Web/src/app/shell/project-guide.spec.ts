import { describe, expect, it } from 'bun:test';
import type { NextStep } from '@app/pages/project/next-step';
import type { StepRunner } from '@app/pages/project/step-runner';
import { ProjectGuide } from './project-guide';

const runner = {} as StepRunner;
const DONE: NextStep = { kind: 'allDone', label: 'All steps done', tint: 'ok' };

describe('ProjectGuide', () => {
  it('holds the folder’s next step once the project shell publishes it', () => {
    const guide = new ProjectGuide();
    expect(guide.current()).toBeNull();
    guide.publish('dune', DONE, runner);
    expect(guide.current()).toEqual({ folder: 'dune', next: DONE, runner });
  });

  it('a newer publish replaces the last one, whatever its folder', () => {
    const guide = new ProjectGuide();
    guide.publish('dune', DONE, runner);
    const next = {
      kind: 'waiting',
      step: 'speakers',
      label: 'Speakers · 2 queued',
      tint: 'busy',
      tooltip: null,
      pills: [],
    } satisfies NextStep;
    guide.publish('emma', next, runner);
    expect(guide.current()?.folder).toBe('emma');
    expect(guide.current()?.next).toBe(next);
  });

  it('clearing a folder forgets it, but not a project published since', () => {
    const guide = new ProjectGuide();
    guide.publish('emma', DONE, runner);
    guide.clear('dune');
    expect(guide.current()?.folder).toBe('emma');
    guide.clear('emma');
    expect(guide.current()).toBeNull();
  });
});
