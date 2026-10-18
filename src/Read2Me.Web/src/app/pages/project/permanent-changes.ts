import type { ProjectStatusDto } from '@app/api';
import type { ConfirmOptions, PermanentChange } from '@app/ui/dialogs';

/**
 * Reread's confirm, shared by the Overview row and the Book overflow: it keeps its own title and
 * body, and names the confirmed and skipped steps it clears when any checkpoint row exists
 * (guidance-primitives spec §7). Always red: everything the user made on the Book goes.
 */
export function rereadConfirm(checkpoints: ProjectStatusDto['checkpoints']): ConfirmOptions {
  const steps =
    Object.keys(checkpoints).length > 0 ? ' Clears your confirmed and skipped steps.' : '';
  return {
    title: 'Reread the book?',
    message:
      'This deletes all book data — attribution, generated audio, and edits — and ' +
      `reprocesses the source file from scratch.${steps} This cannot be undone.`,
    destructive: true,
    confirmLabel: 'Delete and reread',
  };
}

/**
 * The project card's permanent-change confirm (guidance-primitives spec §7): Remove cover always
 * asks and is red, since the image the user uploaded is deleted.
 */
export function removeCoverChange(): PermanentChange {
  return {
    action: 'Remove cover',
    scope: 'Deletes the cover image.',
    cost: 'Getting it back means uploading it again.',
    destructive: true,
  };
}
