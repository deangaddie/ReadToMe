import { describe, expect, it } from 'bun:test';
import type { SeriesCastDto } from '@app/api';
import {
  boardCells,
  charactersTabLabel,
  linkedNames,
  otherLinks,
  pickerOptions,
  seriesCharacterLabels,
  suggestedRowCells,
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

describe('Link suggestions (#223)', () => {
  /** Four's Watson suggested in Watson's row; Adventures has two Holmeses, an ambiguous match. */
  function suggesting(): SeriesCastDto {
    const board = cast();
    board.rows[1]!.cells[1] = {
      folder: 'four',
      linked: null,
      suggested: { characterId: 'f-watson', name: 'John Watson' },
    };
    board.unlinked.four = board.unlinked.four!.map((c) =>
      c.characterId === 'f-watson' ? { ...c, nameMatches: ['sc-watson'] } : c,
    );
    board.unlinked.adventures = [
      { characterId: 'a-irene', name: 'Irene Adler', isNarrator: false, nameMatches: [] },
      { characterId: 'a-holmes', name: 'Holmes', isNarrator: false, nameMatches: ['sc-holmes'] },
      {
        characterId: 'a-sherlock',
        name: 'Sherlock',
        isNarrator: false,
        nameMatches: ['sc-holmes'],
      },
    ];
    board.suggestedRows = [
      {
        label: 'Narrator',
        members: [
          { folder: 'scarlet', characterId: 's-narrator', name: 'Narrator' },
          { folder: 'four', characterId: 'f-narrator', name: 'Narrator' },
        ],
      },
    ];
    board.suggestionCount = 2;
    return board;
  }

  it('gives a suggested cell its Character', () => {
    const board = suggesting();
    expect(boardCells(board, board.rows[1]!)[1]).toMatchObject({
      kind: 'suggested',
      characterId: 'f-watson',
      name: 'John Watson',
    });
  });

  it('lists the Characters whose names match the row first, marked, in an ambiguous cell', () => {
    const board = suggesting();
    const adventures = boardCells(board, board.rows[0]!)[2]!;
    expect(
      adventures.kind === 'empty' && adventures.options.map((o) => [o.name, o.matches]),
    ).toEqual([
      ['Holmes', true],
      ['Sherlock', true],
      ['Irene Adler', false],
    ]);
  });

  it('marks no match in another row’s cell, nor in the Share as new picker', () => {
    const board = suggesting();
    const watsonCell = boardCells(board, board.rows[1]!)[2]!;
    expect(watsonCell.kind === 'empty' && watsonCell.options.map((o) => o.matches)).toEqual([
      false,
      false,
      false,
    ]);
    expect(pickerOptions(board, 'adventures').map((o) => o.name)).toEqual([
      'Irene Adler',
      'Holmes',
      'Sherlock',
    ]);
  });

  it('gives a suggested row one cell per book: its member there, or none', () => {
    const board = suggesting();
    expect(
      suggestedRowCells(board, board.suggestedRows[0]!).map((c) => [
        c.book.folder,
        c.member?.characterId ?? null,
      ]),
    ).toEqual([
      ['scarlet', 's-narrator'],
      ['four', 'f-narrator'],
      ['adventures', null],
    ]);
  });

  it('counts suggestions on the Characters tab', () => {
    expect(charactersTabLabel(0)).toBe('Characters');
    expect(charactersTabLabel(1)).toBe('Characters · 1 suggestion');
    expect(charactersTabLabel(3)).toBe('Characters · 3 suggestions');
  });
});
