import type {
  SeriesCharacterBookDto,
  SeriesCharacterDto,
  SeriesVoiceDeleteBlockDto,
  VoiceDto,
} from '@app/api';
import { bookLabel } from './series-shelf';
import { list } from './permanent-changes';

/**
 * The inline series Voice editor's rules (#221, Character linking #208 variant B), apart from its
 * element so specs read them without a DOM. The editor shows the series copy's Voices; its writes
 * go to that book's per-book routes, and the host copies each into the other linked books.
 */

/** The series copy's own book: where the editor's writes go, and its Character there. */
export function seriesCopyBook(detail: SeriesCharacterDto): SeriesCharacterBookDto | null {
  return detail.books.find((b) => b.folder === detail.seriesCopyFolder) ?? null;
}

/**
 * Where an edit lands (#213 2): the linked books that don't differ from the series. A book that
 * differs shows its own badge instead.
 */
export function savedInCount(detail: SeriesCharacterDto): number {
  return detail.books.filter((b) => !b.differs).length;
}

/** The toast after an edit: "Saved in 3 books", or "Saved" when only one book holds the Voice. */
export function savedInLabel(detail: SeriesCharacterDto): string {
  const count = savedInCount(detail);
  return count > 1 ? `Saved in ${count} books` : 'Saved';
}

/** The rename prompt's hint: which books the new name is saved in. */
export function renameHint(detail: SeriesCharacterDto): string {
  return `Saved in ${list(detail.books.filter((b) => !b.differs).map((b) => b.title))}.`;
}

/** Why a Voice can't be deleted, for the note under it and the disabled Delete; null when it can. */
export function deleteNote(block: SeriesVoiceDeleteBlockDto | null | undefined): string | null {
  if (!block) return null;
  const titles = list(block.books.map((b) => b.title));
  return block.reason === 'PositionalRules'
    ? `Can't delete: Voice Rules in ${titles} use it. Remove those rules first.`
    : `Can't delete the last Voice: ${titles} would have no default Voice.`;
}

/** The refused delete of one Voice, if any. */
export function deleteBlockOf(
  detail: SeriesCharacterDto,
  voice: VoiceDto,
): SeriesVoiceDeleteBlockDto | null {
  return detail.deleteBlocks[voice.id] ?? null;
}

/** One "Default in Book N" column: the book, its heading, and whether its default is `voice`. */
export interface DefaultCell {
  book: SeriesCharacterBookDto;
  heading: string;
  checked: boolean;
}

/** A linked book's column heading: "Default in Book 2". */
export function defaultHeading(book: SeriesCharacterBookDto): string {
  return `Default in ${bookLabel(book.position)}`;
}

/** The Default in Book N columns for one Voice row, in Series position order. */
export function defaultCells(detail: SeriesCharacterDto, voice: VoiceDto): DefaultCell[] {
  return detail.books.map((book) => ({
    book,
    heading: defaultHeading(book),
    checked: book.defaultVoiceId === voice.id,
  }));
}
