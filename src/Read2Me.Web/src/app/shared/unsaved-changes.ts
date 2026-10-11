import type { ConfirmService } from '@app/ui/dialogs';

export const DIRTY_GUARD_MESSAGE = 'You have unsaved changes. Leave and discard them?';

/**
 * The settings editors' discard prompt (design §6.4): the router asks it before leaving a
 * `guardUnsaved` route whose page reports `hasUnsavedChanges()`, and a master–detail page asks it
 * before switching its editor to another item.
 */
export function confirmDiscard(confirm: ConfirmService): Promise<boolean> {
  return confirm.confirm({
    title: 'Discard changes?',
    message: DIRTY_GUARD_MESSAGE,
    confirmLabel: 'Discard',
    destructive: true,
  });
}
