import { describe, expect, it } from 'bun:test';
import {
  type DraftRow,
  draftChanged,
  draftPositions,
  draftRows,
  positionsProblem,
  rebaseDraft,
  saveBlockedReason,
} from './positions-draft';
import type { SeriesView } from './series-shelf';

const project = (folderName: string, title: string) =>
  ({ folderName, title }) as SeriesView['members'][number]['project'];

const holmes: SeriesView = {
  id: 'h',
  name: 'Sherlock Holmes',
  members: [
    { project: project('scarlet', 'A Study in Scarlet'), position: 1 },
    { project: project('four', 'The Sign of the Four'), position: 2.5 },
  ],
};

const typed = (texts: string[]): DraftRow[] =>
  draftRows(holmes).map((r, i) => ({ ...r, text: texts[i] ?? r.text }));

describe('draftRows()', () => {
  it('starts from the saved numbers, formatted', () => {
    expect(draftRows(holmes).map((r) => [r.folder, r.title, r.text])).toEqual([
      ['scarlet', 'A Study in Scarlet', '1'],
      ['four', 'The Sign of the Four', '2.5'],
    ]);
  });
});

describe('positionsProblem()', () => {
  it('is null for a valid set, swaps and gaps included', () => {
    expect(positionsProblem(typed(['2', '1']))).toBeNull();
    expect(positionsProblem(typed(['0', '7.25']))).toBeNull();
    expect(positionsProblem(typed([' 1 ', '1.50']))).toBeNull();
  });

  it('names both books in a tie', () => {
    expect(positionsProblem(typed(['2', '2.0']))).toBe(
      'A Study in Scarlet and The Sign of the Four are both Book 2.',
    );
  });

  it('refuses a bad format, naming the book', () => {
    for (const bad of ['', '-1', '1.125', 'two', '1,5', '.5']) {
      expect(positionsProblem(typed(['1', bad]))).toBe(
        'The Sign of the Four: a book number is 0 or more, with at most two decimal places.',
      );
    }
  });
});

describe('draftChanged()', () => {
  it('is false until a number really changes', () => {
    expect(draftChanged(typed([]))).toBe(false);
    expect(draftChanged(typed(['1.0', '2.50']))).toBe(false);
    expect(draftChanged(typed(['1', '3']))).toBe(true);
  });
});

describe('saveBlockedReason()', () => {
  it('blocks on a problem, then on nothing changed', () => {
    expect(saveBlockedReason(typed(['2', '2']))).toBe(
      'A Study in Scarlet and The Sign of the Four are both Book 2.',
    );
    expect(saveBlockedReason(typed([]))).toBe('No book number has changed.');
    expect(saveBlockedReason(typed(['3', '1']))).toBeNull();
  });
});

describe('rebaseDraft()', () => {
  it('follows the members after a re-read, keeping what was typed for the books still there', () => {
    const reread: SeriesView = {
      ...holmes,
      members: [
        { project: project('four', 'The Sign of the Four'), position: 2 },
        { project: project('adventures', 'The Adventures of Sherlock Holmes'), position: 3 },
      ],
    };
    expect(rebaseDraft(typed(['4', '1']), reread)).toEqual([
      { folder: 'four', title: 'The Sign of the Four', position: 2, text: '1' },
      { folder: 'adventures', title: 'The Adventures of Sherlock Holmes', position: 3, text: '3' },
    ]);
  });
});

describe('draftPositions()', () => {
  it('is the whole set, keyed by folder', () => {
    expect(draftPositions(typed(['2', '1.5']))).toEqual({ scarlet: 2, four: 1.5 });
  });
});
