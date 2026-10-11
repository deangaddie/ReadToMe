import { describe, expect, it } from 'bun:test';
import { adoptStyles } from './styles';

describe('adoptStyles', () => {
  it('adds the CSS to the document as one more adopted sheet', () => {
    const before = document.adoptedStyleSheets.length;
    adoptStyles('.x-adopted { color: red; }');
    adoptStyles('.x-adopted-too { color: blue; }');
    const added = document.adoptedStyleSheets.slice(before);
    expect(added).toHaveLength(2);
    expect(added[0]?.cssRules[0]?.cssText).toContain('.x-adopted');
  });
});
