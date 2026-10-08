import type { R2mElement } from './element';

export interface DialogOptions {
  /**
   * `sheet` docks the dialog to the bottom edge (the preflight bottom sheet, spec §5.3);
   * `fullscreen` fills the viewport (MatDialog's `r2m-fullscreen-dialog` panel).
   */
  variant?: 'sheet' | 'fullscreen';
  /** `none` disables light dismiss and Escape (MatBottomSheet's `disableClose`). */
  closedBy?: 'any' | 'none';
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
  dialog.setAttribute('closedby', options.closedBy ?? 'any');
  if (options.closedBy === 'none') {
    // `closedby` is new (Chromium 134, Firefox 141); older engines still close on Escape.
    dialog.addEventListener('cancel', (e) => e.preventDefault());
  }
  dialog.append(content);
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
