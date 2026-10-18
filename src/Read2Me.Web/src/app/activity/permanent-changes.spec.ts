import { describe, expect, it } from 'bun:test';
import type { JobView } from '@app/ui/job';
import { cancelJobChange } from './permanent-changes';

const job = (overrides: Partial<JobView> = {}): JobView => ({
  id: 'attribution',
  kind: 'attribution',
  label: 'Attributing',
  state: 'running',
  ...overrides,
});

describe('cancelJobChange (guidance-primitives 14)', () => {
  it.each([
    ['a queue with work left', job({ queued: 11, processing: 1 }), '12 queued jobs'],
    ['one job left', job({ queued: 0, processing: 1 }), '1 queued job'],
    ['a queue the hub reports as empty', job({ queued: 0 }), '0 queued jobs'],
    [
      'a batch with a total: what is not yet done or failed',
      job({ id: 'voiceBatch', kind: 'voiceBatch', total: 12, completed: 4, failed: 1 }),
      '7 queued jobs',
    ],
  ])('%s: always asks, neutral, counting the work still ahead', (_, j, count) => {
    expect(cancelJobChange(j)).toEqual({
      action: 'Cancel queued jobs',
      scope: `Cancels ${count} across all projects.`,
      cost: 'Queue them again to resume.',
      destructive: false,
    });
  });

  it('a job without queue counts (a local run) names itself instead', () => {
    expect(
      cancelJobChange(job({ id: 'voicePrompt:c1', kind: 'voicePrompt', label: 'Voice prompt' })),
    ).toEqual({
      action: 'Cancel job',
      scope: 'Cancels “Voice prompt”.',
      cost: 'Start it again to resume.',
      destructive: false,
    });
  });
});
