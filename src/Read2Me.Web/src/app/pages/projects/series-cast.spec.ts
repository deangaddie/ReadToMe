import { describe, expect, it } from 'bun:test';
import type { SeriesCastDto } from '@app/api';
import {
  boardCells,
  linkedNames,
  otherLinks,
  pickerOptions,
  seriesCharacterLabels,
} from './series-cast';

const scarlet = { folder: 'scarlet', title: 'A Study in Scarlet', position: 1 };
const four = { folder: 'four', title: 'The Sign of the Four', position: 2 };
const adventures = {
  folder: 'adventures',
  title: 'The Adventures of Sherlock Holmes',
  position: 3,
};

const linked = (characterId: string, name: string) => ({ characterId, name, differs: false });

/** Holmes linked in Scarlet and Four; Watson only in Scarlet; Adventures links nothing. */
function cast(): SeriesCastDto {
  return {
    books: [scarlet, four, adventures],
    rows: [
      {
        seriesCharacterId: 'sc-holmes',
        label: 'Sherlock Holmes',
        isNarrator: false,
        cells: [
          { folder: 'scarlet', linked: linked('s-holmes', 'Sherlock Holmes'), suggested: null },
          { folder: 'four', linked: linked('f-holmes', 'Holmes'), suggested: null },
          { folder: 'adventures', linked: null, suggested: null },
        ],
      },
      {
        seriesCharacterId: 'sc-watson',
        label: 'Dr. Watson',
        isNarrator: false,
        cells: [
          { folder: 'scarlet', linked: linked('s-watson', 'Dr. Watson'), suggested: null },
          { folder: 'four', linked: null, suggested: null },
          { folder: 'adventures', linked: null, suggested: null },
        ],
      },
    ],
    suggestedRows: [],
    unlinked: {
      scarlet: [{ characterId: 's-narrator', name: 'Narrator', isNarrator: true, nameMatches: [] }],
      four: [
        { characterId: 'f-narrator', name: 'Narrator', isNarrator: true, nameMatches: [] },
        { characterId: 'f-watson', name: 'John Watson', isNarrator: false, nameMatches: [] },
        // A stale read that still lists Four's Holmes as unlinked: the picker hides it.
        { characterId: 'f-holmes', name: 'Holmes', isNarrator: false, nameMatches: [] },
      ],
      adventures: [],
    },
    suggestionCount: 0,
  };
}

describe('boardCells()', () => {
  it('gives one cell per book in position order: linked with its name, else empty', () => {
    const board = cast();
    const cells = boardCells(board, board.rows[1]!);
    expect(cells.map((c) => [c.book.folder, c.kind])).toEqual([
      ['scarlet', 'linked'],
      ['four', 'empty'],
      ['adventures', 'empty'],
    ]);
    expect(cells[0]).toMatchObject({ characterId: 's-watson', name: 'Dr. Watson', differs: false });
  });

  it('gives an empty cell the picker of its book', () => {
    const board = cast();
    const four = boardCells(board, board.rows[1]!)[1]!;
    expect(four.kind === 'empty' && four.options.map((o) => o.name)).toEqual([
      'Narrator',
      'John Watson',
    ]);
  });

  it('shows a suggestion when the read carries one', () => {
    const board = cast();
    board.rows[1]!.cells[1] = {
      folder: 'four',
      linked: null,
      suggested: { characterId: 'f-watson', name: 'John Watson' },
    };
    expect(boardCells(board, board.rows[1]!)[1]).toMatchObject({
      kind: 'suggested',
      name: 'John Watson',
    });
  });

  it('treats a book the row has no cell for as empty', () => {
    const board = cast();
    board.rows[1]!.cells = board.rows[1]!.cells.slice(0, 1);
    expect(boardCells(board, board.rows[1]!).map((c) => c.kind)).toEqual([
      'linked',
      'empty',
      'empty',
    ]);
  });
});

describe('pickerOptions()', () => {
  it('lists the book’s unlinked Characters, the Narrator among them', () => {
    expect(pickerOptions(cast(), 'scarlet').map((o) => o.name)).toEqual(['Narrator']);
  });

  it('hides a Character already linked in that book, so a row never gets a second one', () => {
    expect(pickerOptions(cast(), 'four').map((o) => o.characterId)).toEqual([
      'f-narrator',
      'f-watson',
    ]);
  });

  it('is empty for a book with nothing to pick', () => {
    expect(pickerOptions(cast(), 'adventures')).toEqual([]);
    expect(pickerOptions(cast(), 'unknown')).toEqual([]);
  });
});

describe('otherLinks(), linkedNames(), seriesCharacterLabels()', () => {
  it('names the row’s Characters in the other books', () => {
    const board = cast();
    expect(otherLinks(board, board.rows[0]!, 'four')).toEqual([
      { name: 'Sherlock Holmes', title: 'A Study in Scarlet' },
    ]);
    expect(otherLinks(board, board.rows[0]!, 'adventures')).toHaveLength(2);
  });

  it('names a book’s linked Characters, for Leave and Move to', () => {
    expect(linkedNames(cast(), 'scarlet')).toEqual(['Sherlock Holmes', 'Dr. Watson']);
    expect(linkedNames(cast(), 'adventures')).toEqual([]);
  });

  it('lists every Series character’s label, for Delete series', () => {
    expect(seriesCharacterLabels(cast())).toEqual(['Sherlock Holmes', 'Dr. Watson']);
  });
});
