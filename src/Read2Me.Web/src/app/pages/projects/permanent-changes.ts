import type { SeriesVoiceLossDto } from '@app/api';
import type { ConfirmOptions } from '@app/ui/dialogs';
import { bookLabel, type Membership } from './series-shelf';

/**
 * The Projects page's permanent-change confirms (web.md conventions 12), worded per Series
 * lifecycle (#206). Joining, creating, renaming and saving numbers ask nothing; Leave and Move to
 * ask (neutral: every book keeps its Voices), deleting a series and Remove poster ask in red, and deleting a member
 * project adds a line. Like `rereadConfirm`, each keeps its own title and button label, which a
 * `PermanentChange`'s "Action?" title cannot say ("Remove Emma from Austen?"), so each returns
 * `ConfirmOptions`.
 */

/** "A", "A and B", "A, B and C". */
export function list(names: readonly string[]): string {
  if (names.length <= 1) return names.join('');
  return `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`;
}

export interface LeaveInput {
  /** The book's title. */
  title: string;
  seriesName: string;
  /** This book's series-linked Characters, which lose their link; null when they couldn't be read. */
  linked: readonly string[] | null;
}

function leaveMessage({ seriesName, linked }: LeaveInput): string {
  const links =
    linked === null
      ? 'Any of its Characters linked to the series lose their link. This book keeps its own copy ' +
        'of their Voices.'
      : linked.length === 0
        ? 'No Characters in this book are linked to the series.'
        : linked.length === 1
          ? `${linked[0]} loses its series link. This book keeps its own copy of its Voices.`
          : `${list(linked)} lose their series link. This book keeps its own copy of their Voices.`;
  return `${links} The other books in ${seriesName} keep their numbers.`;
}

/** Remove from <series> (Series lifecycle 9). */
export function leaveConfirm(input: LeaveInput): ConfirmOptions {
  return {
    title: `Remove ${input.title} from ${input.seriesName}?`,
    message: leaveMessage(input),
    confirmLabel: 'Remove from series',
    destructive: false,
  };
}

/** Move to <series>: Leave + Join, asking Leave's confirm (Series lifecycle 8). */
export function moveConfirm(input: LeaveInput & { to: string }): ConfirmOptions {
  return {
    title: `Move ${input.title} to ${input.to}?`,
    message: `It leaves ${input.seriesName}. ${leaveMessage(input)}`,
    confirmLabel: `Move to ${input.to}`,
    destructive: false,
  };
}

/** Delete series (Series lifecycle 12): names the member books, each kept with its Voices. */
export function deleteSeriesConfirm(input: {
  name: string;
  titles: readonly string[];
  /** An uploaded poster goes too; a fallback cover belongs to its book and stays. */
  hasPoster?: boolean;
  /** The Series characters' labels, whose links go. */
  linked?: readonly string[];
}): ConfirmOptions {
  const poster = input.hasPoster ?? false;
  const linked = input.linked ?? [];
  const links = linked.length === 0 ? 'links' : `the links of ${list(linked)}`;
  return {
    title: `Delete series ${input.name}?`,
    message:
      input.titles.length === 0
        ? poster
          ? 'The series has no books. Its name and poster are removed, and there is no History ' +
            'to bring them back.'
          : 'The series has no books. Its name is removed, and there is no History to bring it back.'
        : `Every book is kept, with its Characters and Voices: ${list(input.titles)}. ` +
          `The series’ name, ${poster ? 'poster, ' : ''}book numbers and ${links} are removed, ` +
          'and there is no History to bring them back.',
    confirmLabel: 'Delete series',
    destructive: true,
  };
}

/**
 * Remove poster (Series lifecycle 11): red, since the uploaded image has no History. Says what
 * shows instead: the cover of the lowest-positioned book (`fallbackPosition`), or the placeholder.
 */
export function removePosterConfirm(input: {
  name: string;
  fallbackPosition: number | null;
}): ConfirmOptions {
  const instead =
    input.fallbackPosition === null
      ? 'a placeholder with its name'
      : `the cover of ${bookLabel(input.fallbackPosition)}`;
  return {
    title: `Remove the poster of ${input.name}?`,
    message:
      'The uploaded image is deleted, and there is no History to bring it back. ' +
      `The series shows ${instead} instead.`,
    confirmLabel: 'Remove poster',
    destructive: true,
  };
}

/** The project delete, with the series line for a member (Series lifecycle 13). */
export function deleteProjectConfirm(
  title: string,
  membership: Membership | undefined,
): ConfirmOptions {
  const series = membership
    ? ` Part of ${membership.name} (${bookLabel(membership.position)}); the other books keep their Voices.`
    : '';
  return {
    title: `Delete ${title}?`,
    message:
      'The project folder and everything in it are removed: the book file, characters, ' +
      `voices and generated audio. This cannot be undone.${series}`,
    destructive: true,
    confirmLabel: 'Delete project',
  };
}

// ---- the series cast board (Character linking #208, 7) -----------------------------------------

/** “A”, “A” and “B”; “no Voices” for none. */
function voiceNames(names: readonly string[]): string {
  return names.length === 0 ? 'no Voices' : list(names.map((n) => `“${n}”`));
}

/** Link: whom the Character will share with and which Voices each side gains (the preview read). */
export function linkConfirm(input: {
  name: string;
  /** The book the Character is in. */
  title: string;
  /** The Series character's label. */
  label: string;
  /** The row's Characters in the other books. */
  others: readonly { name: string; title: string }[];
  bookGains: readonly string[];
  seriesGains: readonly string[];
}): ConfirmOptions {
  const others = list(input.others.map((o) => `${o.name} in ${o.title}`));
  const theOthers = input.others.length === 1 ? 'the other book gains' : 'the other books gain';
  return {
    title: `Link ${input.name} to ${input.label}?`,
    message:
      `${input.name} will share Voices with ${others}. ` +
      `${input.title} gains ${voiceNames(input.bookGains)}; ${theOthers} ${voiceNames(input.seriesGains)}. ` +
      'Each is a History step in its book. Names, aliases, delivery cues and Voice Rules stay ' +
      'each book’s own.',
    confirmLabel: 'Link',
    destructive: false,
  };
}

/** Unlink: the book keeps its copy, and edits stop crossing books; the last link takes the row. */
export function unlinkConfirm(input: {
  name: string;
  title: string;
  label: string;
  /** The other books still linked to the row. */
  otherTitles: readonly string[];
}): ConfirmOptions {
  const keeps = `${input.title} keeps its own copy of ${input.name}’s Voices, so nothing in it breaks.`;
  return {
    title: `Unlink ${input.name} from ${input.label}?`,
    message:
      input.otherTitles.length === 0
        ? `${keeps} No other book is linked, so the series character ${input.label} is removed.`
        : `${keeps} From now on, Voice edits there no longer reach ${list(input.otherTitles)}, ` +
          'and theirs no longer reach it.',
    confirmLabel: 'Unlink',
    destructive: false,
  };
}

/** Share as new: a Series character is made from this Character's Voices. */
export function shareConfirm(input: {
  name: string;
  title: string;
  seriesName: string;
}): ConfirmOptions {
  return {
    title: `Share ${input.name} across ${input.seriesName}?`,
    message: `A series character is made from the Voices of ${input.name} in ${input.title}.`,
    confirmLabel: 'Share as new',
    destructive: false,
  };
}

/**
 * Share and link a suggested row (#223): one Series character made from the lowest-positioned
 * book's Character, the others linked to it, and what each book gains (the preview read).
 */
export function shareAndLinkConfirm(input: {
  /** The suggested row's label. */
  label: string;
  seriesName: string;
  /** Each member, lowest position first: its name, its book, and the Voices that book gains. */
  books: readonly { name: string; title: string; gains: readonly string[] }[];
}): ConfirmOptions {
  const members = list(input.books.map((b) => `${b.name} in ${b.title}`));
  const gains = input.books.map((b) => `${b.title} gains ${voiceNames(b.gains)}`).join('; ');
  return {
    title: `Share and link ${input.label} across ${input.seriesName}?`,
    message:
      `${members} become one series character, sharing their Voices. ${gains}. ` +
      'Each is a History step in its book. Names, aliases, delivery cues and Voice Rules stay ' +
      'each book’s own.',
    confirmLabel: 'Share and link',
    destructive: false,
  };
}

/**
 * Delete a shared Voice from the inline series Voice editor (#221), in red: it goes from every
 * linked book, each of whose History can bring it back there only.
 */
export function deleteSeriesVoiceConfirm(
  voiceName: string,
  titles: readonly string[],
): ConfirmOptions {
  return {
    title:
      titles.length > 1
        ? `Delete "${voiceName}" from ${titles.length} books?`
        : `Delete "${voiceName}"?`,
    message:
      `It is removed from ${list(titles)}. Each book's History can bring it back in that book ` +
      'only. A book whose default it is moves to its oldest remaining Voice.',
    confirmLabel: 'Delete Voice',
    destructive: true,
  };
}

/**
 * What a push or take removes, from its preview read: "Four loses “Pipe”, and the Voice Rule on it."
 * per book, then the Voice Rules that stay.
 */
function lossLines(losses: readonly SeriesVoiceLossDto[]): string {
  if (losses.length === 0) return 'Voice Rules stay each book’s own.';
  const lines = losses.map((l) => {
    const rules =
      l.ruleCount === 0
        ? ''
        : l.ruleCount === 1
          ? `, and the Voice Rule on ${l.voices.length === 1 ? 'it' : 'them'}`
          : `, and the ${l.ruleCount} Voice Rules on ${l.voices.length === 1 ? 'it' : 'them'}`;
    return `${l.title} loses ${voiceNames(l.voices)}${rules}.`;
  });
  return `${lines.join(' ')} Other Voice Rules stay each book’s own.`;
}

/**
 * Push (#222): a book that differs from series copies its Voices into every other linked book.
 * Neutral: each replaced book's History can bring its own copy back.
 */
export function pushSeriesVoicesConfirm(input: {
  /** The Series character's label. */
  label: string;
  /** The book whose copy is pushed. */
  title: string;
  /** The other linked books, whose Voices are replaced. */
  otherTitles: readonly string[];
  /** What each replaced book loses, from the push preview. */
  losses?: readonly SeriesVoiceLossDto[];
}): ConfirmOptions {
  const many = input.otherTitles.length > 1;
  return {
    title: `Use ${input.title}’s copy for all?`,
    message:
      `${input.label}’s Voices in ${list(input.otherTitles)} are replaced by the copy in ` +
      `${input.title}. ${many ? 'Each of those books records' : 'That book records'} a ` +
      '“Push series voices” History step, which can bring its own copy back. ' +
      lossLines(input.losses ?? []),
    confirmLabel: 'Use this copy for all',
    destructive: false,
  };
}

/** Take (#222): the series copy replaces this book's Voices. Neutral, as push. */
export function takeSeriesVoicesConfirm(input: {
  label: string;
  /** The book that differs, whose Voices are replaced. */
  title: string;
  /** The book the series copy is read from. */
  copyTitle: string;
  /** What this book loses, from the take preview. */
  losses?: readonly SeriesVoiceLossDto[];
}): ConfirmOptions {
  return {
    title: `Use the series copy in ${input.title}?`,
    message:
      `${input.label}’s Voices in ${input.title} are replaced by the series copy, from ` +
      `${input.copyTitle}. ${input.title} records a “Take series voices” History step, which can ` +
      'bring its own copy back. ' +
      lossLines(input.losses ?? []),
    confirmLabel: 'Use the series copy',
    destructive: false,
  };
}
