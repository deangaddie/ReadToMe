import { afterEach, describe, expect, it } from 'bun:test';
import { openDialog, requestDialogClose } from './dialog';

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

function titled(id?: string): HTMLElement {
  const content = document.createElement('div');
  const title = document.createElement('h2');
  title.className = 'r2m-dialog__title';
  title.textContent = 'Rename chapter';
  if (id) title.id = id;
  content.append(title);
  return content;
}

describe('openDialog naming', () => {
  it('names the dialog by its title, giving the title an id', async () => {
    const content = titled();
    void openDialog(content);
    await Promise.resolve();
    const dialog = document.querySelector('dialog')!;
    const title = content.querySelector('h2')!;
    expect(title.id).not.toBe('');
    expect(dialog.getAttribute('aria-labelledby')).toBe(title.id);
    expect(dialog.hasAttribute('aria-label')).toBe(false);
  });

  it('keeps an id the title already has', async () => {
    void openDialog(titled('my-title'));
    await Promise.resolve();
    expect(document.querySelector('dialog')?.getAttribute('aria-labelledby')).toBe('my-title');
  });

  it('gives each dialog its own title id', async () => {
    void openDialog(titled());
    void openDialog(titled());
    await Promise.resolve();
    const [a, b] = [...document.querySelectorAll('dialog')];
    expect(a?.getAttribute('aria-labelledby')).not.toBe(b?.getAttribute('aria-labelledby'));
  });

  it('names the dialog by ariaLabel over its title', async () => {
    void openDialog(titled(), { ariaLabel: 'Edit with AI' });
    await Promise.resolve();
    const dialog = document.querySelector('dialog')!;
    expect(dialog.getAttribute('aria-label')).toBe('Edit with AI');
    expect(dialog.hasAttribute('aria-labelledby')).toBe(false);
  });
});

function guarded(canClose: () => boolean | Promise<boolean>): HTMLElement {
  return Object.assign(document.createElement('div'), { canClose });
}

function cancel(dialog: HTMLDialogElement): Event {
  const event = new Event('cancel', { cancelable: true });
  dialog.dispatchEvent(event);
  return event;
}

describe('openDialog canClose guard', () => {
  it('lets a cancel through when the content has no canClose', async () => {
    void openDialog(document.createElement('div'));
    await Promise.resolve();
    expect(cancel(document.querySelector('dialog')!).defaultPrevented).toBe(false);
  });

  it('stays open when canClose returns false', async () => {
    void openDialog(guarded(() => false));
    await Promise.resolve();
    const dialog = document.querySelector('dialog')!;
    expect(cancel(dialog).defaultPrevented).toBe(true);
    await Promise.resolve();
    expect(dialog.open).toBe(true);
    expect(dialog.isConnected).toBe(true);
  });

  it('closes with nothing when canClose returns true', async () => {
    const result = openDialog<string>(guarded(() => true));
    await Promise.resolve();
    expect(cancel(document.querySelector('dialog')!).defaultPrevented).toBe(true);
    expect(await result).toBeUndefined();
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('waits for an async canClose and asks once while it is pending', async () => {
    let answer: (ok: boolean) => void = () => {};
    let asked = 0;
    const result = openDialog<string>(
      guarded(() => {
        asked++;
        return new Promise<boolean>((resolve) => (answer = resolve));
      }),
    );
    await Promise.resolve();
    const dialog = document.querySelector('dialog')!;
    cancel(dialog);
    expect(cancel(dialog).defaultPrevented).toBe(true);
    expect(asked).toBe(1);
    expect(dialog.open).toBe(true);
    answer(false);
    await new Promise((r) => setTimeout(r));
    expect(dialog.open).toBe(true);
    cancel(dialog);
    expect(asked).toBe(2);
    answer(true);
    expect(await result).toBeUndefined();
  });

  it('routes requestDialogClose through the guard', async () => {
    let ok = false;
    const content = guarded(() => ok);
    const result = openDialog<string>(content);
    await Promise.resolve();
    requestDialogClose(content);
    await new Promise((r) => setTimeout(r));
    expect(document.querySelector('dialog')?.open).toBe(true);
    ok = true;
    requestDialogClose(content);
    expect(await result).toBeUndefined();
  });
});
