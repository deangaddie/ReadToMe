import { PromptCatalogEntry } from '@app/api';
import { canReset, insertToken, isDirty, tokenLiteral } from './prompt-editing';

function entry(overrides: Partial<PromptCatalogEntry> = {}): PromptCatalogEntry {
  return {
    kind: 'voice',
    title: 'Character Voice Prompt',
    description: 'Voice design for one voice.',
    tokens: ['book_title', 'character_name'],
    expectedResponse: null,
    template: 'DEFAULT',
    defaultTemplate: 'DEFAULT',
    isOverridden: false,
    warnings: [],
    ...overrides,
  };
}

describe('prompt editing', () => {
  it('tokenLiteral wraps the name in double braces', () => {
    expect(tokenLiteral('book_title')).toBe('{{book_title}}');
  });

  describe('insertToken', () => {
    it('inserts at the caret and lands the caret after the token', () => {
      expect(insertToken('Hello world', 'book_title', 6)).toEqual({
        text: 'Hello {{book_title}}world',
        caret: 20,
      });
    });

    it('replaces the selection', () => {
      expect(insertToken('Hello world', 'book_title', 6, 11)).toEqual({
        text: 'Hello {{book_title}}',
        caret: 20,
      });
    });

    it('accepts a backwards selection and clamps out-of-range positions', () => {
      expect(insertToken('abc', 'x', 3, 1)).toEqual({ text: 'a{{x}}', caret: 6 });
      expect(insertToken('abc', 'x', -4, 99)).toEqual({ text: '{{x}}', caret: 5 });
    });

    it('appends when there is no caret', () => {
      expect(insertToken('abc', 'x', null)).toEqual({ text: 'abc{{x}}', caret: 8 });
    });
  });

  describe('isDirty', () => {
    it('compares the draft with the resolved template, not the default', () => {
      const overridden = entry({ template: 'MINE', isOverridden: true });
      expect(isDirty(overridden, 'MINE')).toBe(false);
      expect(isDirty(overridden, 'DEFAULT')).toBe(true);
      expect(isDirty(entry(), 'DEFAULT')).toBe(false);
      expect(isDirty(entry(), 'DEFAULT ')).toBe(true);
    });
  });

  describe('canReset', () => {
    it('is off while the default is in use and untouched', () => {
      expect(canReset(entry(), 'DEFAULT')).toBe(false);
    });

    it('is on for a stored override, even when the draft equals the default text', () => {
      expect(canReset(entry({ template: 'MINE', isOverridden: true }), 'DEFAULT')).toBe(true);
    });

    it('is on for unsaved edits over the default, so Reset discards them', () => {
      expect(canReset(entry(), 'DEFAULT edited')).toBe(true);
    });
  });
});
