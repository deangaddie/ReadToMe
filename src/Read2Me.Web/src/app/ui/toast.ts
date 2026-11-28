import { html, render } from 'lit-html';
import { topmostModal } from '@app/core/dialog';
import { announce } from './announcer';
import { icon } from './partials';

export type ToastSeverity = 'success' | 'info' | 'warn' | 'error';

/** Subset of RFC 9457 ProblemDetails the API returns; `detail` is shown verbatim (design §8). */
export interface ProblemLike {
  title?: string;
  detail?: string;
  status?: number;
}

/** `silent` shows the toast without announcing it: for a caller that has already said it. */
export interface ToastOptions {
  silent?: boolean;
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

/** The countdown to a toast's own dismissal. */
interface Countdown {
  /** For a toast dismissed by hand. */
  stop(): void;
  /** The stack was re-parented, which can take focus or the pointer away with no event to say so. */
  moved(): void;
}

/**
 * Whether a `focusout` took the focus out of `box`: not a move within it, and not the window losing
 * focus, which fires one too while the active element stays.
 */
function focusLeft(box: HTMLElement, e: FocusEvent): boolean {
  return !box.contains(e.relatedTarget as Node | null) && !box.contains(document.activeElement);
}

/**
 * Calls `dismiss` once `toast` has had `ms` without focus inside it or the pointer over it: either
 * one pauses the countdown, which runs out what it had left when both have gone.
 */
function countdown(toast: HTMLElement, ms: number, dismiss: () => void): Countdown {
  let left = ms;
  let since = Date.now();
  let timer: ReturnType<typeof setTimeout> | undefined = setTimeout(dismiss, left);
  let focused = false;
  let hovered = false;
  let stopped = false;
  const update = () => {
    if (stopped) return;
    const held = focused || hovered;
    if (held && timer !== undefined) {
      clearTimeout(timer);
      timer = undefined;
      left -= Date.now() - since;
    } else if (!held && timer === undefined) {
      since = Date.now();
      timer = setTimeout(dismiss, left);
    }
  };
  toast.addEventListener('focusin', () => {
    focused = true;
    update();
  });
  toast.addEventListener('focusout', (e) => {
    if (!focusLeft(toast, e)) return;
    focused = false;
    update();
  });
  toast.addEventListener('pointerenter', () => {
    hovered = true;
    update();
  });
  toast.addEventListener('pointerleave', () => {
    hovered = false;
    update();
  });
  return {
    stop: () => {
      stopped = true;
      clearTimeout(timer);
    },
    moved: () => {
      focused = toast.contains(document.activeElement);
      // The pointer enters again if it is still over the toast.
      hovered = false;
      update();
    },
  };
}

/**
 * Global toasts (design §7, §8). A `popover="manual"` stack in the top layer replaces MatSnackBar:
 * a region named Notifications, each message said by the announcer. While a modal dialog is open
 * the stack is mounted inside it, so a toast raised there can be seen, clicked and heard.
 */
export class ToastService {
  #stack: HTMLElement | null = null;
  /** What had focus before it entered the stack: where a dismissed toast hands it back. */
  #focusFrom: HTMLElement | null = null;
  readonly #countdowns = new WeakMap<Element, Countdown>();

  success(message: string, options?: ToastOptions): void {
    this.show('success', message, options);
  }

  /** With `actions`, the toast offers them as buttons and stays up longer. */
  info(message: string, actions?: ToastAction[]): void {
    this.show('info', message, { actions });
  }

  warn(message: string, options?: ToastOptions): void {
    this.show('warn', message, options);
  }

  error(message: string): void {
    this.show('error', message);
  }

  /** Error toast for a ProblemDetails body: `detail`, else `title`, else a generic message. */
  problem(problem: ProblemLike | null | undefined): void {
    const message = problem?.detail?.trim() || problem?.title?.trim() || 'Request failed';
    this.show('error', message);
  }

  private show(
    severity: ToastSeverity,
    message: string,
    { actions = [], silent = false }: ToastOptions & { actions?: ToastAction[] | undefined } = {},
  ): void {
    const stack = this.stack();
    const toast = document.createElement('div');
    toast.className = `r2m-toast r2m-toast--${severity}`;
    const dismiss = () => {
      // Moved on before the toast goes, or focus would fall to <body>.
      if (toast.contains(document.activeElement)) {
        const next = toast.nextElementSibling?.querySelector('button');
        (next ?? this.#focusFrom)?.focus();
      }
      count.stop();
      toast.remove();
      if (!stack.childElementCount) {
        stack.hidePopover();
        this.#focusFrom = null;
      }
    };
    const text = document.createElement('p');
    text.className = 'r2m-toast__text';
    text.textContent = message;
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'r2m-icon-button r2m-toast__dismiss';
    close.setAttribute('aria-label', 'Dismiss');
    close.dataset['tooltip'] = 'Dismiss';
    close.addEventListener('click', dismiss);
    render(html`${icon('close')}`, close);
    toast.append(text, close);
    if (actions.length) {
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
      toast.append(row);
    }
    stack.append(toast);
    this.raise(stack);
    if (!silent)
      announce(
        actions.length ? `${message} Actions available` : message,
        severity === 'error' ? 'assertive' : 'polite',
      );
    const ms = actions.length ? WITH_ACTIONS_MS : DURATION_MS[severity];
    const count = countdown(toast, ms, dismiss);
    this.#countdowns.set(toast, count);
  }

  /**
   * Puts the stack on top, in the topmost modal (everything outside one is inert) or else on the
   * body. A modal it is mounted in passes it on as it closes.
   */
  private raise(stack: HTMLElement): void {
    const home = topmostModal() ?? document.body;
    if (stack.parentElement !== home) {
      // Removal hides the popover, so the re-show below opens it again.
      home.append(stack);
      for (const toast of stack.children) this.#countdowns.get(toast)?.moved();
      if (home !== document.body) {
        home.addEventListener(
          'close',
          () => {
            if (stack.parentElement === home) this.raise(stack);
          },
          { once: true },
        );
      }
    } else if (stack.contains(document.activeElement)) {
      // It is showing, and hiding it would drop the focus.
      return;
    }
    // Re-show so the stack moves above whatever opened in the top layer after it.
    stack.hidePopover();
    if (stack.childElementCount) stack.showPopover();
  }

  private stack(): HTMLElement {
    if (!this.#stack) {
      const stack = document.createElement('div');
      stack.className = 'r2m-toasts';
      stack.popover = 'manual';
      stack.setAttribute('role', 'region');
      stack.setAttribute('aria-label', 'Notifications');
      stack.addEventListener('focusin', (e) => {
        const from = e.relatedTarget as HTMLElement | null;
        if (from && !stack.contains(from)) this.#focusFrom = from;
      });
      stack.addEventListener('focusout', (e) => {
        if (focusLeft(stack, e)) this.#focusFrom = null;
      });
      document.body.append(stack);
      this.#stack = stack;
    }
    return this.#stack;
  }
}
