import { describe, expect, it } from 'bun:test';
import { isComposing } from './keys';

describe('isComposing', () => {
  it.each([
    { key: 'Enter', isComposing: true },
    // Safari ends the composition before the confirming keydown: only the key code still says so.
    { key: 'Enter', keyCode: 229 },
    { key: 'Escape', isComposing: true },
  ])('is true while an IME composes ($key)', (init) => {
    expect(isComposing(new KeyboardEvent('keydown', init))).toBe(true);
  });

  it('is false for an ordinary key', () => {
    expect(isComposing(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13 }))).toBe(false);
  });
});
