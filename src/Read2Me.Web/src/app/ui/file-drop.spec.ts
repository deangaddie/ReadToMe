import { afterEach, describe, expect, it } from 'bun:test';
import type { RejectedFile } from './file-drop';
import './file-drop';

/** happy-dom has no DataTransfer/DragEvent; a plain Event with a dataTransfer-shaped payload is enough. */
function drop(el: Element, files: File[]) {
  const event = new Event('drop', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'dataTransfer', { value: { files } });
  el.dispatchEvent(event);
}

afterEach(() => document.body.replaceChildren());

async function mount() {
  const zone = document.createElement('r2m-file-drop');
  zone.accept = '.epub,audio/*';
  zone.maxBytes = 100;
  zone.label = 'Drop a book';
  const files: File[] = [];
  const rejected: RejectedFile[] = [];
  zone.addEventListener('files', (e) => files.push(...(e as CustomEvent<File[]>).detail));
  zone.addEventListener('rejected', (e) =>
    rejected.push(...(e as CustomEvent<RejectedFile[]>).detail),
  );
  document.body.append(zone);
  await zone.rendered();
  return { zone, files, rejected };
}

describe('r2m-file-drop', () => {
  it('renders the label and a picker button', async () => {
    const { zone } = await mount();
    expect(zone.querySelector('.r2m-file-drop__label')?.textContent).toBe('Drop a book');
    expect(zone.querySelector('button')?.textContent?.trim()).toBe('Choose file');
    expect(zone.querySelector<HTMLInputElement>('input[type=file]')?.accept).toBe('.epub,audio/*');
  });

  it('accepts matching files and rejects wrong type or oversize', async () => {
    const { zone, files, rejected } = await mount();

    drop(zone, [new File(['x'], 'book.EPUB')]);
    drop(zone, [new File(['x'], 'clip.wav', { type: 'audio/wav' })]);
    drop(zone, [new File(['x'], 'notes.txt', { type: 'text/plain' })]);
    drop(zone, [new File(['x'.repeat(101)], 'big.epub')]);

    expect(files.map((f) => f.name)).toEqual(['book.EPUB', 'clip.wav']);
    expect(rejected.map((r) => [r.file.name, r.reason])).toEqual([
      ['notes.txt', 'type'],
      ['big.epub', 'size'],
    ]);
  });

  it('keeps only the first file when not multiple', async () => {
    const { zone, files } = await mount();

    drop(zone, [new File(['a'], 'a.epub'), new File(['b'], 'b.epub')]);

    expect(files.map((f) => f.name)).toEqual(['a.epub']);
  });

  it('a read-only reason turns the picker off with the reason and ignores a drop', async () => {
    const { zone, files, rejected } = await mount();
    zone.readOnly = 'Viewing a past step';
    await zone.rendered();

    const button = zone.querySelector('button')!;
    expect(button.disabled).toBe(true);
    expect(button.closest('.r2m-blockable')?.getAttribute('data-tooltip')).toBe(
      'Viewing a past step',
    );
    expect(zone.querySelector<HTMLInputElement>('input[type=file]')!.disabled).toBe(true);
    drop(zone, [new File(['x'], 'book.epub')]);
    expect(files).toEqual([]);
    expect(rejected).toEqual([]);

    zone.readOnly = null;
    await zone.rendered();
    drop(zone, [new File(['x'], 'book.epub')]);
    expect(files.map((f) => f.name)).toEqual(['book.epub']);
  });
});
