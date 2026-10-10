import type { NarrationReleasePreviewDto } from '@app/api';
import type { ConfirmOptions } from '@app/ui/dialogs';

/**
 * Release narration's wording, beside the bulk assign's (`bulk-assign.ts`): the confirm says how
 * many narration-only paragraphs become dialog and how much audio goes with them. The figures come
 * from the preview read, never from the loaded rows: a part spans chapters that were never opened.
 */

function plural(n: number): string {
  return n === 1 ? '' : 's';
}

/** The confirm to show, or null when the scope holds no narration-only paragraph. */
export function releaseConfirm(
  nodeTitle: string,
  preview: NarrationReleasePreviewDto,
): ConfirmOptions | null {
  const { paragraphs, itemsWithAudio } = preview;
  if (paragraphs === 0) return null;

  let message = `Release ${paragraphs} narration paragraph${plural(paragraphs)} in ${nodeTitle}?`;
  if (itemsWithAudio > 0) {
    message += ` ${itemsWithAudio} ${itemsWithAudio === 1 ? 'has' : 'have'} audio, which will be discarded.`;
  }

  return {
    title: 'Release narration',
    message,
    confirmLabel: 'Release',
    destructive: itemsWithAudio > 0,
  };
}

/** The success toast: the release never attributes, so it names the two ways on. */
export function releaseDoneMessage(preview: NarrationReleasePreviewDto): string {
  const n = preview.paragraphs;
  return n === 1
    ? 'Released 1 paragraph. Assign its speaker or attribute it.'
    : `Released ${n} paragraphs. Assign their speakers or attribute them.`;
}

export const NOTHING_TO_RELEASE = 'No narration-only paragraphs here — nothing to release.';
