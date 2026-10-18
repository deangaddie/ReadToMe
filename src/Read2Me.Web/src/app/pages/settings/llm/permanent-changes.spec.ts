import { describe, expect, it } from 'bun:test';
import { removeStepChange } from './permanent-changes';

describe('removeStepChange (guidance-primitives 14)', () => {
  it.each([
    [0, 'Gemma', 'Removes step 1 (Gemma) and its settings.'],
    [2, 'Qwen thinking', 'Removes step 3 (Qwen thinking) and its settings.'],
  ])('index %i: always asks, red, numbering the rung from one', (index, name, scope) => {
    expect(removeStepChange(index, name)).toEqual({
      action: 'Remove step',
      scope,
      cost: 'Adding it back means choosing its config, thinking and style again.',
      destructive: true,
    });
  });
});
