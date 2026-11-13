import { describe, expect, it } from 'bun:test';
import { removeCoverChange } from './permanent-changes';

describe('removeCoverChange (guidance-primitives 14)', () => {
  it('always asks, red: the image the user uploaded goes', () => {
    expect(removeCoverChange()).toEqual({
      action: 'Remove cover',
      scope: 'Deletes the cover image.',
      cost: 'Getting it back means uploading it again.',
      destructive: true,
    });
  });
});
