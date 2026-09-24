import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MAT_SNACK_BAR_DATA, MatSnackBarRef } from '@angular/material/snack-bar';

export type ToastSeverity = 'success' | 'info' | 'warn' | 'error';

export interface ToastData {
  severity: ToastSeverity;
  message: string;
}

const ICONS: Record<ToastSeverity, string> = {
  success: 'check_circle',
  info: 'info',
  warn: 'warning',
  error: 'error',
};

/**
 * Snackbar surface with severity styling (design §7). Opened only by {@link ToastService};
 * the panel itself is made transparent (styles.scss, `.r2m-toast-panel`) so this host paints the
 * soft status colour.
 */
@Component({
  selector: 'r2m-toast',
  imports: [MatIconModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-toast', '[class]': '"r2m-toast--" + data.severity', role: 'status' },
  template: `
    <mat-icon class="r2m-toast__icon" aria-hidden="true">{{ icon }}</mat-icon>
    <span class="r2m-toast__message">{{ data.message }}</span>
    <button mat-button type="button" class="r2m-toast__dismiss" (click)="ref.dismiss()">
      Dismiss
    </button>
  `,
  styles: `
    :host {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      min-height: 48px;
      padding: var(--r2m-space-2) var(--r2m-space-2) var(--r2m-space-2) var(--r2m-space-4);
      border-radius: var(--r2m-radius-md);
      border-left: 4px solid var(--_fg);
      box-shadow: var(--r2m-shadow-2);
      font-size: var(--r2m-text-md);
      color: var(--r2m-text);
      background: var(--_bg);
      --_fg: var(--r2m-status-info);
      --_bg: var(--r2m-status-info-soft);
    }
    :host(.r2m-toast--success) {
      --_fg: var(--r2m-status-ok);
      --_bg: var(--r2m-status-ok-soft);
    }
    :host(.r2m-toast--warn) {
      --_fg: var(--r2m-status-warn);
      --_bg: var(--r2m-status-warn-soft);
    }
    :host(.r2m-toast--error) {
      --_fg: var(--r2m-status-error);
      --_bg: var(--r2m-status-error-soft);
    }
    .r2m-toast__icon {
      color: var(--_fg);
      flex: 0 0 auto;
    }
    .r2m-toast__message {
      flex: 1 1 auto;
      overflow-wrap: anywhere;
    }
    .r2m-toast__dismiss {
      flex: 0 0 auto;
      color: var(--_fg);
    }
  `,
})
export class Toast {
  readonly data = inject<ToastData>(MAT_SNACK_BAR_DATA);
  readonly ref = inject(MatSnackBarRef<Toast>);
  readonly icon = ICONS[this.data.severity];
}
