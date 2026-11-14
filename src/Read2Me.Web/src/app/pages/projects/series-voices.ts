import type {
  SeriesCharacterBookDto,
  SeriesCharacterDto,
  SeriesVoiceDeleteBlockDto,
  SeriesVoiceLossDto,
  VoiceDto,
} from '@app/api';
import { sameFolder } from '@app/live/folders';
import { bookLabel } from './series-shelf';
import type { ConfirmOptions } from '@app/ui/dialogs';
import { list, pushSeriesVoicesConfirm, takeSeriesVoicesConfirm } from './permanent-changes';

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
 * Where an edit landed (#213 2), read after it: the linked books that don't differ from the series.
 * A book whose copy failed shows its own badge instead.
 */
export function savedInCount(detail: SeriesCharacterDto): number {
  return detail.books.filter((b) => !b.differs).length;
}

/** The toast after an edit: "Saved in 3 books", or "Saved" when only one book holds the Voice. */
export function savedInLabel(detail: SeriesCharacterDto): string {
  const count = savedInCount(detail);
  return count > 1 ? `Saved in ${count} books` : 'Saved';
}

/**
 * The rename prompt's hint: which books the new name is saved in. Every linked book: an edit made
 * in the editor brings a book that differs back in line (#222).
 */
export function renameHint(detail: SeriesCharacterDto): string {
  return `Saved in ${list(detail.books.map((b) => b.title))}.`;
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

/** A differing book's callout in the editor (#222): who differs, which copy shows, and its push. */
export interface DiffersCallout {
  book: SeriesCharacterBookDto;
  message: string;
  /** "Use <book>'s copy for all". */
  pushLabel: string;
  /** The other linked books, which a push replaces. */
  otherTitles: string[];
}

/** One callout per linked book whose Voices differ from the series copy, in Series position order. */
export function differsCallouts(detail: SeriesCharacterDto): DiffersCallout[] {
  const copy = seriesCopyBook(detail);
  return detail.books
    .filter((b) => b.differs)
    .map((book) => ({
      book,
      message:
        `${book.title} holds a different copy of these Voices. This editor shows the series copy` +
        `${copy ? `, from ${copy.title}` : ''}. Adding or changing a Voice here brings ${book.title} back in ` +
        'line.',
      pushLabel: `Use ${book.title}’s copy for all`,
      otherTitles: detail.books.filter((b) => b !== book).map((b) => b.title),
    }));
}

/**
 * The confirm for a push or take of `folder`'s Voices (#222), from the editor's read: a push names
 * every other linked book, a take the book the series copy is read from. Null when `folder` isn't
 * linked in that read.
 */
export function resolveConfirm(
  detail: SeriesCharacterDto,
  folder: string,
  label: string,
  how: 'push' | 'take',
  losses: readonly SeriesVoiceLossDto[] = [],
): ConfirmOptions | null {
  // Folders compare ignoring case, as the host's do.
  const book = detail.books.find((b) => sameFolder(b.folder, folder));
  if (!book) return null;
  if (how === 'push')
    return pushSeriesVoicesConfirm({
      label,
      title: book.title,
      otherTitles: detail.books.filter((b) => b !== book).map((b) => b.title),
      losses,
    });
  return takeSeriesVoicesConfirm({
    losses,
    label,
    title: book.title,
    copyTitle: seriesCopyBook(detail)?.title ?? 'the other books',
  });
}

/** The book Cast page's callout for a linked Character whose Voices differ from series (#222). */
export function castDiffersText(name: string, seriesName: string): string {
  return (
    `${name}’s Voices in this book differ from the other books in ${seriesName}. Use this ` +
    'book’s copy for all of them, or take the series copy here.'
  );
}
