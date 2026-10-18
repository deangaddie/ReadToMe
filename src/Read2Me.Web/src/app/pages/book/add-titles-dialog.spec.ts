import { afterEach, describe, expect, it } from 'bun:test';
import { type AddTitlesDialogData, openAddTitlesDialog } from './add-titles-dialog';

/**
 * The Add titles dialog (guidance-primitives spec §5): one box per level that adds navigation, all
 * ticked; an unticked level says what listeners lose; a level the book lacks says why it is absent.
 * It closes with the ticked levels in the order Apply sends them.
 */
afterEach(() => document.body.replaceChildren());

const FULL: AddTitlesDialogData = { volumes: 2, parts: 4, chapters: 12, advice: null };

async function open(data: AddTitlesDialogData) {
  const result = openAddTitlesDialog(data);
  await Promise.resolve();
  const dialog = document.querySelector('r2m-add-titles-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
const box = (dialog: Element, level: string) =>
  dialog.querySelector<HTMLInputElement>(`input[name=${level}]`);
const untick = async (dialog: Element & { rendered(): Promise<void> }, level: string) => {
  const input = box(dialog, level)!;
  input.checked = false;
  input.dispatchEvent(new Event('change'));
  await dialog.rendered();
};
const apply = (dialog: Element) =>
  dialog.querySelector<HTMLButtonElement>('[data-action=apply-titles]')!;

describe('r2m-add-titles-dialog', () => {
  it('offers every level that adds navigation, all ticked, with nothing lost', async () => {
    const { dialog } = await open(FULL);
    for (const level of ['book', 'volume', 'part', 'chapter']) {
      expect(box(dialog, level)?.checked).toBe(true);
    }
    expect(dialog.querySelectorAll('.titles__lost')).toHaveLength(0);
    expect(dialog.querySelectorAll('.titles__missing')).toHaveLength(0);
  });

  it('a level the book lacks has no box, only a muted line saying why', async () => {
    const { dialog } = await open({ volumes: 1, parts: 1, chapters: 2, advice: null });
    expect(box(dialog, 'book')).not.toBeNull();
    expect(box(dialog, 'volume')).toBeNull();
    expect(box(dialog, 'part')).toBeNull();
    expect(box(dialog, 'chapter')).not.toBeNull();
    expect(Array.from(dialog.querySelectorAll('.titles__missing'), text)).toEqual([
      'One volume, so no volume titles.',
      'One part per volume, so no part titles.',
    ]);
  });

  it('an unticked level names what listeners lose', async () => {
    const { dialog } = await open(FULL);
    await untick(dialog, 'chapter');
    expect(Array.from(dialog.querySelectorAll('.titles__lost'), text)).toEqual([
      "Listeners won't hear chapter names as each chapter starts.",
    ]);
  });

  it('Apply closes with the ticked levels, book first and chapter last', async () => {
    const { dialog, result } = await open(FULL);
    await untick(dialog, 'part');
    apply(dialog).click();
    expect(await result).toEqual(['book', 'volume', 'chapter']);
  });

  it('Apply is disabled while nothing is ticked', async () => {
    const { dialog } = await open({ volumes: 1, parts: 1, chapters: 1, advice: null });
    expect(apply(dialog).disabled).toBe(false);
    await untick(dialog, 'book');
    expect(apply(dialog).disabled).toBe(true);
  });

  it('shows the advice above the button', async () => {
    const advice = 'Usually done after Structure: a reread drops inserted titles.';
    const { dialog } = await open({ ...FULL, advice });
    // Before the button: the advice reads first, never inside a confirm.
    const line = dialog.querySelector('.r2m-advice')!;
    expect(text(line)).toBe(advice);
    expect(line.compareDocumentPosition(apply(dialog)) & Node.DOCUMENT_POSITION_FOLLOWING).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
  });

  it('Cancel closes with null', async () => {
    const { dialog, result } = await open(FULL);
    dialog.querySelector<HTMLButtonElement>('[data-action=cancel]')!.click();
    expect(await result).toBeNull();
  });
});
