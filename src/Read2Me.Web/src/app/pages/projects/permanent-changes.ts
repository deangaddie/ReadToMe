import type { ConfirmOptions } from '@app/ui/dialogs';
import { bookLabel, type Membership } from './series-shelf';

/**
 * The Projects page's permanent-change confirms (web.md conventions 12), worded per Series
 * lifecycle (#206). Joining, creating, renaming and saving numbers ask nothing; Leave and Move to
 * ask (neutral: every book keeps its Voices), deleting a series asks in red, and deleting a member
 * project adds a line. Like `rereadConfirm`, each keeps its own title and button label, which a
 * `PermanentChange`'s "Action?" title cannot say ("Remove Emma from Austen?"), so each returns
 * `ConfirmOptions`.
 */

/** "A", "A and B", "A, B and C". */
function list(names: readonly string[]): string {
  if (names.length <= 1) return names.join('');
  return `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`;
}

export interface LeaveInput {
  /** The book's title. */
  title: string;
  seriesName: string;
  /** This book's series-linked Characters, which lose their link. */
  linked: readonly string[];
}

function leaveMessage({ seriesName, linked }: LeaveInput): string {
  const links =
    linked.length === 0
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
}): ConfirmOptions {
  return {
    title: `Delete series ${input.name}?`,
    message:
      input.titles.length === 0
        ? 'The series has no books. Its name is removed, and there is no History to bring it back.'
        : `Every book is kept, with its Characters and Voices: ${list(input.titles)}. ` +
          'The series’ name, book numbers and links are removed, and there is no History to ' +
          'bring them back.',
    confirmLabel: 'Delete series',
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
