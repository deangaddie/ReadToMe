import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Toast, ToastData, ToastSeverity } from './toast';

/** Subset of RFC 9457 ProblemDetails the API returns; `detail` is shown verbatim (design §8). */
export interface ProblemLike {
  title?: string;
  detail?: string;
  status?: number;
}

const DURATION_MS: Record<ToastSeverity, number> = {
  success: 3000,
  info: 3000,
  warn: 6000,
  error: 6000,
};

/**
 * Global toasts (design §7, §8): outcomes the user did not initiate on this screen, and
 * ProblemDetails from failed requests. The only MatSnackBar consumer in the app — an ESLint rule
 * keeps `@angular/material/snack-bar` imports inside `app/ui/`.
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly snackBar = inject(MatSnackBar);

  success(message: string): void {
    this.show('success', message);
  }

  info(message: string): void {
    this.show('info', message);
  }

  warn(message: string): void {
    this.show('warn', message);
  }

  error(message: string): void {
    this.show('error', message);
  }

  /** Error toast for a ProblemDetails body: `detail`, else `title`, else a generic message. */
  problem(problem: ProblemLike | null | undefined): void {
    const message = problem?.detail?.trim() || problem?.title?.trim() || 'Request failed';
    this.show('error', message);
  }

  private show(severity: ToastSeverity, message: string): void {
    const data: ToastData = { severity, message };
    this.snackBar.openFromComponent(Toast, {
      data,
      duration: DURATION_MS[severity],
      panelClass: 'r2m-toast-panel',
      horizontalPosition: 'end',
      verticalPosition: 'bottom',
    });
  }
}
