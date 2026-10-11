import { afterEach, describe, expect, it } from 'bun:test';
import { settle } from '../../../../testing/fake-navigation';
import type { VoiceScopeDialog } from './voice-scope-dialog';
import { openVoiceScopeDialog } from './voice-scope-dialog';
import './voice-scope-dialog';

afterEach(() => document.body.replaceChildren());

async function open() {
  const result = openVoiceScopeDialog();
  await settle();
  const dialog = document.querySelector<VoiceScopeDialog>('r2m-voice-scope-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

describe('r2m-voice-scope-dialog', () => {
  it('answers the scope of the button pressed', async () => {
    const only = await open();
    only.dialog.querySelector<HTMLButtonElement>('[data-scope=only-without]')!.click();
    expect(await only.result).toBe('only-without');

    document.body.replaceChildren();
    const all = await open();
    expect(all.dialog.querySelector('[data-scope=regenerate-all]')?.textContent).toContain(
      'Clear and regenerate all',
    );
    expect(all.dialog.textContent).toContain('This cannot be undone.');
    all.dialog.querySelector<HTMLButtonElement>('[data-scope=regenerate-all]')!.click();
    expect(await all.result).toBe('regenerate-all');
  });

  it('Cancel answers null', async () => {
    const { result, dialog } = await open();
    Array.from(dialog.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Cancel')!
      .click();
    expect(await result).toBeNull();
  });
});
