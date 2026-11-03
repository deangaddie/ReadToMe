import type {
  SeriesCastBookDto,
  SeriesCastDto,
  SeriesCastRowDto,
  SeriesSuggestedRowDto,
  SeriesUnlinkedCharacterDto,
} from '@app/api';

/**
 * The series cast board's rules (Character linking #208, variant B), kept apart from its element
 * so specs read them without a DOM: one row per Series character, one cell per book in Series
 * position order, each cell linked, suggested (#223) or empty with a picker.
 */

export type BoardCell =
  | {
      kind: 'linked';
      book: SeriesCastBookDto;
      characterId: string;
      name: string;
      differs: boolean;
    }
  | { kind: 'suggested'; book: SeriesCastBookDto; characterId: string; name: string }
  | { kind: 'empty'; book: SeriesCastBookDto; options: PickerOption[] };

/** A picker's Character; `matches`: its names match the row's Series character (#223), so it is listed first. */
export interface PickerOption extends SeriesUnlinkedCharacterDto {
  matches: boolean;
}

/** The Characters of `folder` already linked to some Series character, by id. */
function linkedIds(cast: SeriesCastDto, folder: string): Set<string> {
  const ids = new Set<string>();
  for (const row of cast.rows)
    for (const cell of row.cells)
      if (cell.folder === folder && cell.linked) ids.add(cell.linked.characterId);
  return ids;
}

/**
 * The picker rule (#208, 8): a book's unlinked Characters, never one already linked in that book,
 * so a Series character can't gain a second Character from one book. The Narrator is listed like
 * any other. In a row's cell, the Characters whose names match that row's Series character come
 * first, marked (#223): that is how an ambiguous match, which makes no suggestion, is offered.
 */
export function pickerOptions(
  cast: SeriesCastDto,
  folder: string,
  seriesCharacterId?: string,
): PickerOption[] {
  const linked = linkedIds(cast, folder);
  const options = (cast.unlinked[folder] ?? [])
    .filter((c) => !linked.has(c.characterId))
    .map((c) => ({
      ...c,
      matches: seriesCharacterId !== undefined && c.nameMatches.includes(seriesCharacterId),
    }));
  return [...options.filter((o) => o.matches), ...options.filter((o) => !o.matches)];
}

/** A row's cells in the board's book order; a book the row has no cell for is empty. */
export function boardCells(cast: SeriesCastDto, row: SeriesCastRowDto): BoardCell[] {
  return cast.books.map((book): BoardCell => {
    const cell = row.cells.find((c) => c.folder === book.folder);
    if (cell?.linked)
      return {
        kind: 'linked',
        book,
        characterId: cell.linked.characterId,
        name: cell.linked.name,
        differs: cell.linked.differs,
      };
    if (cell?.suggested)
      return {
        kind: 'suggested',
        book,
        characterId: cell.suggested.characterId,
        name: cell.suggested.name,
      };
    return {
      kind: 'empty',
      book,
      options: pickerOptions(cast, book.folder, row.seriesCharacterId),
    };
  });
}

/** The row's linked Characters in the other books, for the Link and Unlink confirms. */
export function otherLinks(
  cast: SeriesCastDto,
  row: SeriesCastRowDto,
  folder: string,
): { name: string; title: string }[] {
  return boardCells(cast, row).flatMap((cell) =>
    cell.kind === 'linked' && cell.book.folder !== folder
      ? [{ name: cell.name, title: cell.book.title }]
      : [],
  );
}

/** The names of a book's series-linked Characters, in row order, for Leave and Move to. */
export function linkedNames(cast: SeriesCastDto, folder: string): string[] {
  return cast.rows.flatMap((row) =>
    row.cells.flatMap((c) => (c.folder === folder && c.linked ? [c.linked.name] : [])),
  );
}

/** Every Series character's label, for Delete series. */
export function seriesCharacterLabels(cast: SeriesCastDto): string[] {
  return cast.rows.map((r) => r.label);
}

/** A suggested row's cells (#223) in the board's book order: its member in that book, or null. */
export function suggestedRowCells(
  cast: SeriesCastDto,
  row: SeriesSuggestedRowDto,
): { book: SeriesCastBookDto; member: { characterId: string; name: string } | null }[] {
  return cast.books.map((book) => {
    const member = row.members.find((m) => m.folder === book.folder);
    return { book, member: member ? { characterId: member.characterId, name: member.name } : null };
  });
}

/** The series panel's Characters tab: "Characters", or "Characters · 3 suggestions" (#223). */
export function charactersTabLabel(suggestionCount: number): string {
  if (suggestionCount <= 0) return 'Characters';
  return `Characters · ${suggestionCount} ${suggestionCount === 1 ? 'suggestion' : 'suggestions'}`;
}
