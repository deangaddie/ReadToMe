import type { R2mElement } from './element';

export type DialogClosedBy = 'any' | 'closerequest' | 'none';

export interface DialogOptions {
  /**
   * `sheet` docks the dialog to the bottom edge (the preflight bottom sheet, spec §5.3);
   * `fullscreen` fills the viewport (MatDialog's `r2m-fullscreen-dialog` panel).
   */
  variant?: 'sheet' | 'fullscreen';
  /**
   * `any` (the default) closes on Escape and a backdrop click; `closerequest` on Escape only, for a
   * form a stray click must not discard; `none` on neither (MatBottomSheet's `disableClose`).
   */
  closedBy?: DialogClosedBy;
  /**
   * The dialog's accessible name when it shows no `.r2m-dialog__title`; wins over the title. A
   * dialog with a title is named by it (`aria-labelledby`) without this.
   */
  ariaLabel?: string;
}

/**
 * Dialog content that holds unsaved work implements this. Escape, a backdrop click (under
 * `closedby="any"`) and a Cancel button that calls `requestDialogClose` all ask it first; the
 * dialog closes only on `true`. `r2m-close` is not guarded: it is the content's own decision.
 */
export interface ClosableContent {
  canClose(): boolean | Promise<boolean>;
}

let titleIds = 0;

/** Every modal {@link openDialog} has shown and not yet closed, in top-layer order. */
const modals: HTMLDialogElement[] = [];

/**
 * The modal on top, or undefined when none is open. What must be seen or heard while a modal is
 * open belongs in it: everything outside is inert.
 */
export function topmostModal(): HTMLDialogElement | undefined {
  // `close` fires a task after the dialog closes, so one still listed may already be shut.
  return modals.findLast((dialog) => dialog.open && dialog.isConnected);
}

/**
 * Asks the dialog around `content` to close as Escape would, so its `canClose` guard runs: what a
 * Cancel button in guarded content calls instead of emitting `r2m-close`. `requestClose()` is
 * Chromium 134, Firefox 139; older engines get the same `cancel`-then-close by hand.
 */
export function requestDialogClose(content: HTMLElement): void {
  const dialog = content.closest('dialog');
  if (!dialog) return;
  if (typeof dialog.requestClose === 'function') dialog.requestClose();
  else if (dialog.dispatchEvent(new Event('cancel', { cancelable: true }))) dialog.close();
}

/** Older engines close on Escape before `closedby` is honoured; the listener keeps them open. */
function preventCancel(e: Event): void {
  e.preventDefault();
}

/** Names the dialog (top-layer K1): `ariaLabel` when given, else its first `.r2m-dialog__title`. */
function nameDialog(dialog: HTMLDialogElement, content: HTMLElement, ariaLabel?: string): void {
  if (ariaLabel) {
    dialog.setAttribute('aria-label', ariaLabel);
    return;
  }
  const title = content.querySelector('.r2m-dialog__title');
  if (!title) return;
  if (!title.id) title.id = `r2m-dialog-title-${++titleIds}`;
  dialog.setAttribute('aria-labelledby', title.id);
}

/**
 * Runs the content's `canClose` on every `cancel` (Escape, light dismiss, `requestClose()`). The
 * event is always prevented so the guard can be async; the dialog closes when it answers `true`.
 * A `cancel` while it is still asking is swallowed, so a second Escape cannot slip past it.
 */
function guardCancel(dialog: HTMLDialogElement, content: Partial<ClosableContent>): void {
  let asking = false;
  dialog.addEventListener('cancel', (e) => {
    if (!content.canClose) return;
    e.preventDefault();
    if (asking) return;
    asking = true;
    // A guard that throws keeps the dialog open: losing the work is the outcome it exists to stop.
    const canClose = content.canClose.bind(content);
    // The executor runs now, so the hook is asked on this cancel; a throw becomes a rejection.
    void new Promise<boolean>((resolve) => resolve(canClose()))
      .then(
        (ok) => {
          if (ok) dialog.close();
        },
        (error: unknown) => console.error('canClose failed; the dialog stays open', error),
      )
      .finally(() => (asking = false));
  });
}

/**
 * Opens `content` in a native modal `<dialog>` and resolves what it closes with: the content
 * dispatches `r2m-close` with the result; Escape, a backdrop click and Cancel resolve undefined.
 * The browser supplies the focus trap, inertness, Escape, `::backdrop` and focus restoration.
 */
export async function openDialog<T>(
  content: HTMLElement,
  options: DialogOptions = {},
): Promise<T | undefined> {
  const dialog = document.createElement('dialog');
  dialog.className = options.variant ? `r2m-dialog r2m-dialog--${options.variant}` : 'r2m-dialog';
  dialog.append(content);
  // `closedby` is new (Chromium 134, Firefox 141); older engines still close on Escape.
  const closedBy = options.closedBy ?? 'any';
  dialog.setAttribute('closedby', closedBy);
  if (closedBy === 'none') dialog.addEventListener('cancel', preventCancel);
  else guardCancel(dialog, content as Partial<ClosableContent>);
  document.body.append(dialog);
  let result: T | undefined;
  content.addEventListener('r2m-close', (e) => {
    result = (e as CustomEvent<T>).detail;
    dialog.close();
  });
  // Render before showModal so the [autofocus] element exists when the dialog focuses.
  await (content as Partial<R2mElement>).rendered?.();
  nameDialog(dialog, content, options.ariaLabel);
  return new Promise((resolve) => {
    dialog.addEventListener(
      'close',
      () => {
        modals.splice(modals.indexOf(dialog), 1);
        dialog.remove();
        resolve(result);
      },
      { once: true },
    );
    dialog.showModal();
    modals.push(dialog);
  });
}
