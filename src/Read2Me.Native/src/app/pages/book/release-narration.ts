import type { NarrationReleasePreviewDto, NarrationReleaseScope } from '@app/api';
import type { ConfirmOptions } from '@app/ui/dialogs';

/**
 * Release narration's wording, beside the bulk assign's (`bulk-assign.ts`): the confirm says how
 * many narration-only paragraphs become dialog, which speech markers picked them (Chapter and Part
 * only: those releases take only speech-marked paragraphs, ADR 0015) and how much audio goes with
 * them. The figures come from the preview read, never from the loaded rows: a part spans chapters
 * that were never opened.
 */

function plural(n: number): string {
  return n === 1 ? '' : 's';
}

/**
 * Whether the release asks first. A single paragraph with no audio is a one-line fix with
 * nothing to lose, so it is written straight away; anything bigger, or discarding audio, confirms.
 */
export function releaseNeedsConfirm(
  scope: NarrationReleaseScope,
  preview: NarrationReleasePreviewDto,
): boolean {
  return scope !== 'Paragraph' || preview.itemsWithAudio > 0;
}

/** The confirm to show, or null when the scope holds no narration-only paragraph. */
export function releaseConfirm(
  scope: NarrationReleaseScope,
  nodeTitle: string,
  preview: NarrationReleasePreviewDto,
): ConfirmOptions | null {
  const { paragraphs, itemsWithAudio, speechMarkers } = preview;
  if (paragraphs === 0) return null;

  // A paragraph's "title" is its text: the row it was opened from already shows it.
  let message =
    scope === 'Paragraph'
      ? 'Release this narration paragraph?'
      : `Release ${paragraphs} narration paragraph${plural(paragraphs)} in ${nodeTitle}?`;
  if (speechMarkers.length > 0) {
    message += ` Speech markers: ${speechMarkers.map((m) => `${m.marker} ×${m.paragraphs}`).join(', ')}.`;
  }
  if (itemsWithAudio > 0) {
    const subject =
      scope === 'Paragraph'
        ? `${itemsWithAudio} item${plural(itemsWithAudio)}`
        : `${itemsWithAudio}`;
    message += ` ${subject} ${itemsWithAudio === 1 ? 'has' : 'have'} audio, which will be discarded.`;
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

/**
 * The notice when the preview finds nothing. A Chapter or Part may hold narration-only paragraphs
 * that carry no repeated speech marker, so it says nothing looked like speech; the paragraph menu
 * is only offered on narration-only paragraphs, so its wording stays as it was.
 */
export function releaseNothingMessage(scope: NarrationReleaseScope): string {
  return scope === 'Paragraph'
    ? 'No narration-only paragraphs here — nothing to release.'
    : 'No speech-like paragraphs here — nothing to release.';
}
