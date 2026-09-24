import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { DIRTY_GUARD_MESSAGE } from '@app/ui/config-editor-frame/config-editor-frame';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';

/** A page whose in-place editor may hold edits a navigation would lose (design §6.4). */
export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

export function confirmDiscard(confirm: ConfirmService): Promise<boolean> {
  return confirm.confirm({
    title: 'Discard changes?',
    message: DIRTY_GUARD_MESSAGE,
    confirmLabel: 'Discard',
    destructive: true,
  });
}

/** The settings editors' dirty guard: leaving with unsaved edits asks first. */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (page) =>
  !page.hasUnsavedChanges() || confirmDiscard(inject(ConfirmService));
