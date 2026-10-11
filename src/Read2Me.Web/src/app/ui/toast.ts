export type ToastSeverity = 'success' | 'info' | 'warn' | 'error';

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
 * Global toasts (design §7, §8). A `popover="manual"` stack in the top layer replaces MatSnackBar:
 * it sits above modal dialogs too, and `role="status"` announces each message.
 */
export class ToastService {
  #stack: HTMLElement | null = null;

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
    const stack = this.stack();
    const toast = document.createElement('div');
    toast.className = `r2m-toast r2m-toast--${severity}`;
    toast.setAttribute('role', severity === 'error' ? 'alert' : 'status');
    toast.textContent = message;
    stack.append(toast);
    // Re-show so the stack moves above a dialog opened after it.
    stack.hidePopover();
    stack.showPopover();
    setTimeout(() => {
      toast.remove();
      if (!stack.childElementCount) stack.hidePopover();
    }, DURATION_MS[severity]);
  }

  private stack(): HTMLElement {
    if (!this.#stack) {
      this.#stack = document.createElement('div');
      this.#stack.className = 'r2m-toasts';
      this.#stack.popover = 'manual';
      document.body.append(this.#stack);
    }
    return this.#stack;
  }
}
