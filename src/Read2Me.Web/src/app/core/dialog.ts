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
}

/**
 * Lets dialog content change its own dismissal while open: `none` refuses Escape and light dismiss
 * (a form mid-upload), `closerequest` allows Escape only, `any` both. A no-op when `content` is not
 * inside a dialog.
 */
export function setDialogClosedBy(content: HTMLElement, closedBy: DialogClosedBy): void {
  const dialog = content.closest('dialog');
  if (!dialog) return;
  dialog.setAttribute('closedby', closedBy);
  if (closedBy === 'none') dialog.addEventListener('cancel', preventCancel);
  else dialog.removeEventListener('cancel', preventCancel);
}

/** Older engines close on Escape before `closedby` is honoured; the listener keeps them open. */
function preventCancel(e: Event): void {
  e.preventDefault();
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
  setDialogClosedBy(content, options.closedBy ?? 'any');
  document.body.append(dialog);
  let result: T | undefined;
  content.addEventListener('r2m-close', (e) => {
    result = (e as CustomEvent<T>).detail;
    dialog.close();
  });
  // Render before showModal so the [autofocus] element exists when the dialog focuses.
  await (content as Partial<R2mElement>).rendered?.();
  return new Promise((resolve) => {
    dialog.addEventListener(
      'close',
      () => {
        dialog.remove();
        resolve(result);
      },
      { once: true },
    );
    dialog.showModal();
  });
}
