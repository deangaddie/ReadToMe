import { ChangeDetectionStrategy, Component, Injectable, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Red confirm with a delete icon — every delete, reread, clear, regenerate-all (design §8). */
  destructive?: boolean;
}

/**
 * Confirmation dialog (design §7). Open it through `ConfirmService.confirm()`; the promise resolves
 * true only on the confirm button — Cancel, Escape and the backdrop all resolve false.
 */
@Component({
  selector: 'r2m-confirm-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-confirm-dialog',
    '[class.r2m-confirm-dialog--destructive]': 'data.destructive',
  },
  template: `
    <h2 mat-dialog-title class="r2m-confirm-dialog__title">{{ data.title }}</h2>
    <mat-dialog-content class="r2m-confirm-dialog__message">{{ data.message }}</mat-dialog-content>
    <mat-dialog-actions align="end">
      <button
        mat-button
        type="button"
        class="r2m-confirm-dialog__cancel"
        (click)="ref.close(false)"
      >
        {{ data.cancelLabel ?? 'Cancel' }}
      </button>
      <button
        mat-flat-button
        type="button"
        class="r2m-confirm-dialog__confirm"
        cdkFocusInitial
        (click)="ref.close(true)"
      >
        @if (data.destructive) {
          <mat-icon aria-hidden="true">delete_forever</mat-icon>
        }
        {{ data.confirmLabel ?? (data.destructive ? 'Delete' : 'OK') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    :host {
      display: block;
      min-width: 320px;
      max-width: 480px;
    }
    .r2m-confirm-dialog__message {
      white-space: pre-line;
      color: var(--r2m-text-muted);
    }
    :host(.r2m-confirm-dialog--destructive) .r2m-confirm-dialog__confirm {
      --mdc-filled-button-container-color: var(--r2m-status-error);
      --mdc-filled-button-label-text-color: var(--mat-sys-on-error);
      --mat-filled-button-icon-color: var(--mat-sys-on-error);
    }
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmOptions>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<ConfirmDialog, boolean>>(MatDialogRef);
}

@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly dialog = inject(MatDialog);

  async confirm(options: ConfirmOptions): Promise<boolean> {
    const ref = this.dialog.open<ConfirmDialog, ConfirmOptions, boolean>(ConfirmDialog, {
      data: options,
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return (await firstValueFrom(ref.afterClosed())) === true;
  }
}
