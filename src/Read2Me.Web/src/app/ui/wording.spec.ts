import { describe, expect, it } from 'bun:test';
import { plural } from './wording';

describe('plural', () => {
  it.each([
    [0, 's'],
    [1, ''],
    [2, 's'],
    [60, 's'],
  ])('%i takes "%s"', (n, suffix) => {
    expect(plural(n)).toBe(suffix);
  });
});
