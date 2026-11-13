import type { PermanentChange } from '@app/ui/dialogs';

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
