export type ToastSeverity = 'success' | 'info' | 'warn' | 'error';

/** Subset of RFC 9457 ProblemDetails the API returns; `detail` is shown verbatim (design §8). */
export interface ProblemLike {
  title?: string;
  detail?: string;
  status?: number;
}

/** A button on a toast; clicking it runs `run` and dismisses the toast. */
export interface ToastAction {
  label: string;
  run: () => void;
}

const DURATION_MS: Record<ToastSeverity, number> = {
  success: 3000,
  info: 3000,
  warn: 6000,
  error: 6000,
};

/** A toast with actions stays long enough to read them and choose. */
const WITH_ACTIONS_MS = 12000;

/**
 * Global toasts (design §7, §8). A `popover="manual"` stack in the top layer replaces MatSnackBar:
 * it sits above modal dialogs too, and `role="status"` announces each message.
 */
export class ToastService {
  #stack: HTMLElement | null = null;

  success(message: string): void {
    this.show('success', message);
  }

  /** With `actions`, the toast offers them as buttons and stays up longer. */
  info(message: string, actions?: ToastAction[]): void {
    this.show('info', message, actions);
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

  private show(severity: ToastSeverity, message: string, actions: ToastAction[] = []): void {
    const stack = this.stack();
    const toast = document.createElement('div');
    toast.className = `r2m-toast r2m-toast--${severity}`;
    toast.setAttribute('role', severity === 'error' ? 'alert' : 'status');
    const dismiss = () => {
      toast.remove();
      if (!stack.childElementCount) stack.hidePopover();
    };
    if (actions.length === 0) toast.textContent = message;
    else {
      const text = document.createElement('p');
      text.className = 'r2m-toast__text';
      text.textContent = message;
      const row = document.createElement('div');
      row.className = 'r2m-toast__actions';
      for (const action of actions) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'r2m-button r2m-toast__action';
        button.textContent = action.label;
        button.addEventListener('click', () => {
          dismiss();
          action.run();
        });
        row.append(button);
      }
      toast.append(text, row);
    }
    stack.append(toast);
    // Re-show so the stack moves above a dialog opened after it.
    stack.hidePopover();
    stack.showPopover();
    setTimeout(dismiss, actions.length ? WITH_ACTIONS_MS : DURATION_MS[severity]);
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
