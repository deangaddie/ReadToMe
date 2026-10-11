import { afterEach, describe, expect, it } from 'bun:test';
import type { ManualImportRequest } from '@app/api';
import { openManualRereadDialog } from './manual-reread-dialog';
import './manual-reread-dialog';

/**
 * The dialog as a thin view over the pure form: the switches add levels, a mode drops the prefix
 * field, the first failed submit shows the host's wording live, and a good form closes with the
 * request. (Angular tested the form only; the dialog had no spec.)
 */
afterEach(() => document.body.replaceChildren());

async function open() {
  const result = openManualRereadDialog();
  await Promise.resolve();
  const dialog = document.querySelector('r2m-manual-reread-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
const check = (el: Element, name: string, on: boolean) => {
  const box = el.querySelector<HTMLInputElement>(`input[name=${name}]`)!;
  box.checked = on;
  box.dispatchEvent(new Event('change'));
};

describe('r2m-manual-reread-dialog', () => {
  it('starts chapters-only, adds levels with the switches and relabels the parts switch', async () => {
    const { dialog } = await open();
    const headings = () => Array.from(dialog.querySelectorAll('.manual__heading'), text);
    expect(headings()).toEqual(['Book structure', 'Chapter detection']);
    expect(text(dialog.querySelector('label:has(input[name=hasParts])'))).toBe(
      'Book has multiple parts',
    );

    check(dialog, 'hasVolumes', true);
    check(dialog, 'hasParts', true);
    await dialog.rendered();
    expect(headings()).toEqual([
      'Book structure',
      'Volume detection',
      'Part detection',
      'Chapter detection',
    ]);
    expect(text(dialog.querySelector('label:has(input[name=hasParts])'))).toBe(
      'Each volume has multiple parts',
    );
    expect(dialog.querySelectorAll('[role=radiogroup]').length).toBe(3);
  });

  it('a number or roman mode drops the prefix field for that level', async () => {
    const { dialog } = await open();
    expect(dialog.querySelector('input[data-level=chapter]')).not.toBeNull();
    const roman = dialog.querySelector<HTMLInputElement>('input[name=mode-chapter][value=Roman]')!;
    roman.checked = true;
    roman.dispatchEvent(new Event('change'));
    await dialog.rendered();
    expect(dialog.querySelector('input[data-level=chapter]')).toBeNull();
  });

  it('a failed submit shows the host wording, live until it is fixed; a good form closes with the request', async () => {
    const { result, dialog } = await open();
    expect(dialog.querySelector('[role=alert]')).toBeNull();
    dialog.querySelector<HTMLButtonElement>('.manual__submit')!.click();
    await dialog.rendered();
    expect(text(dialog.querySelector('[role=alert]'))).toBe('Chapter prefix cannot be empty.');

    const prefix = dialog.querySelector<HTMLInputElement>('input[data-level=chapter]')!;
    prefix.value = ' Chapter ';
    prefix.dispatchEvent(new Event('input'));
    await dialog.rendered();
    expect(dialog.querySelector('[role=alert]')).toBeNull();

    dialog.querySelector<HTMLButtonElement>('.manual__submit')!.click();
    expect(await result).toEqual<ManualImportRequest>({
      hasMultipleVolumes: false,
      hasMultipleParts: false,
      volume: null,
      part: null,
      chapter: { mode: 'Prefix', prefix: 'Chapter' },
    });
  });

  it('Cancel closes with nothing', async () => {
    const { result, dialog } = await open();
    dialog.querySelector<HTMLButtonElement>('.manual__cancel')!.click();
    expect(await result).toBeNull();
  });
});
