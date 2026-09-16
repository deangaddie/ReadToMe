import { CharacterSummaryDto } from '@app/api';
import {
  initialMergeForm,
  lostVoicesWarning,
  mergeCandidates,
  validateMerge,
} from './merge-options';

function row(name: string, isNarrator = false): CharacterSummaryDto {
  return {
    id: name.toLowerCase(),
    name,
    aliases: [],
    lineCount: 0,
    voiceCount: 0,
    readyVoiceCount: 0,
    isNarrator,
    narratesBook: false,
  };
}

const rows = [row('Narrator', true), row('Alice'), row('Bob'), row('Carol')];

describe('merge dialog rules', () => {
  it('offers every other character except the Narrator', () => {
    expect(mergeCandidates(rows, 'bob').map((c) => c.name)).toEqual(['Alice', 'Carol']);
  });

  it('starts on the first candidate with the alias on', () => {
    expect(initialMergeForm(mergeCandidates(rows, 'bob'))).toEqual({
      survivorId: 'alice',
      addNameAsAlias: true,
    });
    expect(initialMergeForm([])).toEqual({ survivorId: null, addNameAsAlias: true });
  });

  it('requires a survivor from the candidates and never the merged character', () => {
    const candidates = mergeCandidates(rows, 'bob');
    expect(validateMerge({ survivorId: null, addNameAsAlias: true }, candidates, 'bob')).toBe(
      'Select a character to merge into.',
    );
    expect(validateMerge({ survivorId: 'bob', addNameAsAlias: true }, candidates, 'bob')).toBe(
      'A character cannot be merged into itself.',
    );
    expect(validateMerge({ survivorId: 'narrator', addNameAsAlias: true }, candidates, 'bob')).toBe(
      'Select a character to merge into.',
    );
    expect(validateMerge({ survivorId: 'carol', addNameAsAlias: false }, candidates, 'bob')).toBeNull();
  });

  it('warns about lost voices by name, sorted', () => {
    expect(lostVoicesWarning('Bob', [])).toBeNull();
    expect(lostVoicesWarning('Bob', ['Gruff'])).toContain('its voice goes with it');
    const two = lostVoicesWarning('Bob', ['Soft', 'Gruff'])!;
    expect(two).toContain('its 2 voices go with it');
    expect(two).toContain('Gruff, Soft');
  });
});
