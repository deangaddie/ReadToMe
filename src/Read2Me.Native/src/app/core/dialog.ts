import type { R2mElement } from './element';

/**
 * Opens `content` in a native modal `<dialog>` and resolves what it closes with: the content
 * dispatches `r2m-close` with the result; Escape, a backdrop click and Cancel resolve undefined.
 * The browser supplies the focus trap, inertness, Escape, `::backdrop` and focus restoration.
 */
export async function openDialog<T>(content: HTMLElement): Promise<T | undefined> {
  const dialog = document.createElement('dialog');
  dialog.className = 'r2m-dialog';
  dialog.setAttribute('closedby', 'any');
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
