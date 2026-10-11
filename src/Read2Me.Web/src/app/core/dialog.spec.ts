import { afterEach, describe, expect, it } from 'bun:test';
import { openDialog } from './dialog';

// happy-dom's showModal only flips `open`: the focus trap, Escape and the backdrop are the
// browser's, and are covered in E2E.
afterEach(() => document.body.replaceChildren());

describe('openDialog', () => {
  it('shows the content in a modal r2m-dialog', async () => {
    const content = document.createElement('div');
    void openDialog(content);
    await Promise.resolve();
    const dialog = document.querySelector('dialog');
    expect(dialog?.className).toBe('r2m-dialog');
    expect(dialog?.open).toBe(true);
    expect(dialog?.getAttribute('closedby')).toBe('any');
    expect(content.parentElement).toBe(dialog);
  });

  it('resolves with the detail of r2m-close and removes the dialog', async () => {
    const content = document.createElement('div');
    const result = openDialog<string>(content);
    await Promise.resolve();
    content.dispatchEvent(new CustomEvent('r2m-close', { detail: 'saved' }));
    expect(await result).toBe('saved');
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('resolves undefined when the dialog closes without a result', async () => {
    const content = document.createElement('div');
    const result = openDialog<string>(content);
    await Promise.resolve();
    document.querySelector('dialog')?.close();
    expect(await result).toBeUndefined();
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('docks a sheet to the bottom and keeps it open until the content closes it', async () => {
    const content = document.createElement('div');
    void openDialog(content, { variant: 'sheet', closedBy: 'none' });
    await Promise.resolve();
    const dialog = document.querySelector('dialog')!;
    expect(dialog.className).toBe('r2m-dialog r2m-dialog--sheet');
    expect(dialog.getAttribute('closedby')).toBe('none');
    const cancel = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(cancel);
    expect(cancel.defaultPrevented).toBe(true);
  });

  it('waits for the content to render before showing it', async () => {
    let finishRender = () => {};
    const content = Object.assign(document.createElement('div'), {
      rendered: () => new Promise<void>((resolve) => (finishRender = resolve)),
    });
    void openDialog(content);
    await Promise.resolve();
    expect(document.querySelector('dialog')?.open).toBe(false);
    finishRender();
    await Promise.resolve();
    await Promise.resolve();
    expect(document.querySelector('dialog')?.open).toBe(true);
  });
});
