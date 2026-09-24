import { BulkAssignPreviewDto } from '@app/api';
import { ConfirmOptions } from '@app/ui/confirm-dialog/confirm-dialog';

/**
 * The bulk speaker assign's wording (ticket 12; the same sentences Blazor's
 * `BookHierarchyPresenter.BulkConfirmMessage` uses, so both UIs promise the same thing). A null
 * name means a clear throughout. The figures come from the preview read, never from the loaded
 * rows: a selection can cover chapters that were never opened.
 */

/** Noun-suffix pluralisation only, the idiom the wording is written in. */
function plural(n: number): string {
  return n === 1 ? '' : 's';
}

function scope(preview: BulkAssignPreviewDto): string {
  const items = preview.characterItems;
  const paras = preview.paragraphsWithCharacterItems;
  return `${items} dialog line${plural(items)} in ${paras} paragraph${plural(paras)}`;
}

/**
 * The confirm to show, or null when the selection holds no dialog and there is nothing to write.
 * `selectedCount` is how many paragraphs were selected; those the write will not touch (all
 * narration, pauses) are named as skipped.
 */
export function bulkConfirm(
  name: string | null,
  preview: BulkAssignPreviewDto,
  selectedCount: number,
): ConfirmOptions | null {
  const paras = preview.paragraphsWithCharacterItems;
  if (paras === 0) return null;

  const skipped = selectedCount - paras;
  let message =
    name === null
      ? `${scope(preview)} lose their speaker and need attributing again.`
      : `${name} becomes the speaker for ${scope(preview)}. Existing speakers are replaced.`;
  if (skipped > 0) {
    message += ` ${skipped} selected paragraph${plural(skipped)} have no dialog and stay unchanged.`;
  }

  return {
    title: name === null ? 'Clear speakers in selection' : `Assign ${name} to selection`,
    message,
    confirmLabel: name === null ? 'Clear' : 'Assign',
    destructive: name === null,
  };
}

/** The success toast after the write. */
export function bulkDoneMessage(name: string | null, preview: BulkAssignPreviewDto): string {
  const items = preview.characterItems;
  const paras = preview.paragraphsWithCharacterItems;
  return name === null
    ? `Cleared speakers on ${items} line${plural(items)} in ${paras} paragraph${plural(paras)}.`
    : `Assigned ${name} to ${items} line${plural(items)} in ${paras} paragraph${plural(paras)}.`;
}

export const NOTHING_TO_ASSIGN = 'No dialog in the selection — nothing to assign.';
