import { describe, expect, it } from 'bun:test';
import { transcribeChange } from './permanent-changes';

describe('transcribeChange (guidance-primitives 14)', () => {
  it.each([
    ['no transcript yet', ''],
    ['only whitespace', '  \n'],
  ])('%s: nothing is replaced, so no confirm', (_, transcript) => {
    expect(transcribeChange(transcript)).toBeNull();
  });

  it('a transcript exists: asks, red, since the AI answer replaces it', () => {
    expect(transcribeChange('Hello there.')).toEqual({
      action: 'Send to AI',
      scope: 'Replaces the current transcript.',
      cost: 'The text it replaces is not kept.',
      destructive: true,
    });
  });
});
