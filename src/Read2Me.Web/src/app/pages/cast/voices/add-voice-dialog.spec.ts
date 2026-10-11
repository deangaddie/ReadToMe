import { afterEach, describe, expect, it } from 'bun:test';
import { settle } from '../../../../testing/fake-navigation';
import type { AddVoiceDialog } from './add-voice-dialog';
import { openAddVoiceDialog } from './add-voice-dialog';
import './add-voice-dialog';

afterEach(() => document.body.replaceChildren());

async function open(characterName = 'Alice') {
  const result = openAddVoiceDialog({ characterName });
  await settle();
  const dialog = document.querySelector<AddVoiceDialog>('r2m-add-voice-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

describe('r2m-add-voice-dialog', () => {
  it('defaults to a reference voice named after the character', async () => {
    const { result, dialog } = await open();
    expect(dialog.querySelector<HTMLInputElement>('input[type=text]')?.placeholder).toBe('Alice');
    expect(dialog.querySelector<HTMLInputElement>('input[value=reference]')?.checked).toBe(true);
    dialog.querySelector<HTMLButtonElement>('[data-action=add]')!.click();
    expect(await result).toEqual({ name: 'Alice', isGenerated: false });
  });

  it('closes with the typed name and the Prompt source; Enter submits', async () => {
    const { result, dialog } = await open();
    const name = dialog.querySelector<HTMLInputElement>('input[type=text]')!;
    name.value = '  Alice Prompt ';
    name.dispatchEvent(new Event('input'));
    const prompt = dialog.querySelector<HTMLInputElement>('input[value=prompt]')!;
    prompt.checked = true;
    prompt.dispatchEvent(new Event('change'));
    await dialog.rendered();
    name.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(await result).toEqual({ name: 'Alice Prompt', isGenerated: true });
  });

  it('Cancel closes with null', async () => {
    const { result, dialog } = await open();
    Array.from(dialog.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Cancel')!
      .click();
    expect(await result).toBeNull();
  });
});
