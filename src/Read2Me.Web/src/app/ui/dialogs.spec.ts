import { afterEach, describe, expect, it } from 'bun:test';
import {
  ConfirmService,
  PromptService,
  type ConfirmOptions,
  type TextPromptOptions,
} from './dialogs';

// The services open a real <dialog>; happy-dom's showModal only flips `open`, so Escape, the focus
// trap and autofocus are left to E2E.
async function openConfirm(options: ConfirmOptions) {
  const result = new ConfirmService().confirm(options);
  const dialog = document.querySelector('r2m-confirm-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

async function openPrompt(options: TextPromptOptions) {
  const result = new PromptService().text(options);
  const dialog = document.querySelector('r2m-text-prompt-dialog')!;
  await dialog.rendered();
  const input = dialog.querySelector<HTMLInputElement | HTMLTextAreaElement>(
    '.r2m-text-prompt-dialog__input',
  )!;
  const type = async (value: string) => {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await dialog.rendered();
  };
  const confirm = dialog.querySelector<HTMLButtonElement>('.r2m-text-prompt-dialog__confirm')!;
  return { result, dialog, input, type, confirm };
}

afterEach(() => document.body.replaceChildren());

describe('ConfirmService', () => {
  it('shows the title and message with OK / Cancel by default', async () => {
    const { dialog } = await openConfirm({ title: 'Reread?', message: 'This replaces the audio.' });
    expect(dialog.querySelector('.r2m-dialog__title')?.textContent).toBe('Reread?');
    expect(dialog.querySelector('.r2m-confirm-dialog__message')?.textContent).toBe(
      'This replaces the audio.',
    );
    expect(dialog.querySelector('.r2m-confirm-dialog__confirm')?.textContent?.trim()).toBe('OK');
    expect(dialog.querySelector('.r2m-confirm-dialog__cancel')?.textContent?.trim()).toBe('Cancel');
  });

  it('resolves true on the confirm button and closes', async () => {
    const { result, dialog } = await openConfirm({ title: 't', message: 'm' });
    dialog.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__confirm')!.click();
    expect(await result).toBe(true);
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('resolves false on Cancel', async () => {
    const { result, dialog } = await openConfirm({ title: 't', message: 'm' });
    dialog.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__cancel')!.click();
    expect(await result).toBe(false);
  });

  it('resolves false when the dialog is dismissed', async () => {
    const { result } = await openConfirm({ title: 't', message: 'm' });
    document.querySelector('dialog')!.close();
    expect(await result).toBe(false);
  });

  it('a destructive confirm is a danger button with a delete icon, labelled Delete', async () => {
    const { dialog } = await openConfirm({ title: 't', message: 'm', destructive: true });
    const confirm = dialog.querySelector('.r2m-confirm-dialog__confirm')!;
    expect(confirm.classList.contains('r2m-button--danger')).toBe(true);
    expect(confirm.querySelector('.r2m-icon')?.textContent).toBe('delete_forever');
    expect(confirm.textContent).toContain('Delete');
  });

  it('uses the given labels', async () => {
    const { dialog } = await openConfirm({
      title: 't',
      message: 'm',
      confirmLabel: 'Discard',
      cancelLabel: 'Keep editing',
      destructive: true,
    });
    expect(dialog.querySelector('.r2m-confirm-dialog__confirm')?.textContent).toContain('Discard');
    expect(dialog.querySelector('.r2m-confirm-dialog__cancel')?.textContent?.trim()).toBe(
      'Keep editing',
    );
  });
});

describe('PromptService', () => {
  it('starts with the initial text and resolves the trimmed value', async () => {
    const { result, input, type, confirm } = await openPrompt({
      title: 'Rename',
      label: 'Name',
      initial: 'Hardin',
    });
    expect(input.value).toBe('Hardin');
    expect(document.querySelector('.r2m-field__label')?.textContent).toBe('Name');
    await type('  Salvor Hardin ');
    confirm.click();
    expect(await result).toBe('Salvor Hardin');
  });

  it('resolves null on Cancel', async () => {
    const { result, dialog } = await openPrompt({ title: 'Rename', initial: 'x' });
    dialog.querySelector<HTMLButtonElement>('.r2m-text-prompt-dialog__cancel')!.click();
    expect(await result).toBeNull();
  });

  it('resolves null when the dialog is dismissed', async () => {
    const { result } = await openPrompt({ title: 'Rename' });
    document.querySelector('dialog')!.close();
    expect(await result).toBeNull();
  });

  it('Enter submits a single-line prompt', async () => {
    const { result, input, type } = await openPrompt({ title: 'Rename' });
    await type('Gaal');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(await result).toBe('Gaal');
  });

  it('a required prompt cannot be confirmed while blank', async () => {
    const { input, type, confirm } = await openPrompt({ title: 'Name it', required: true });
    expect(confirm.disabled).toBe(true);
    await type('   ');
    expect(confirm.disabled).toBe(true);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(document.querySelector('dialog')).not.toBeNull();
    await type('Anselm');
    expect(confirm.disabled).toBe(false);
  });

  it('a multiline prompt uses a textarea with the placeholder and confirm label', async () => {
    const { input, confirm } = await openPrompt({
      title: 'Hint',
      multiline: true,
      placeholder: 'What should change?',
      confirmLabel: 'Retry',
    });
    expect(input.localName).toBe('textarea');
    expect(input.getAttribute('placeholder')).toBe('What should change?');
    expect(confirm.textContent?.trim()).toBe('Retry');
  });
});
