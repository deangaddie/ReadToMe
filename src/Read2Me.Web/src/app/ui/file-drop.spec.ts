import { afterEach, describe, expect, it } from 'bun:test';
import type { RejectedFile } from './file-drop';
import './file-drop';
import { blockedWith } from '../../testing/description';

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

  it('a blocked gate turns the picker off with the reason and ignores a drop', async () => {
    const { zone, files, rejected } = await mount();
    zone.block = { reason: 'Viewing a past step' };
    await zone.rendered();

    const button = zone.querySelector('button')!;
    expect(blockedWith(button)).toBe('Viewing a past step');
    expect(zone.querySelector<HTMLInputElement>('input[type=file]')!.disabled).toBe(true);
    drop(zone, [new File(['x'], 'book.epub')]);
    expect(files).toEqual([]);
    expect(rejected).toEqual([]);

    zone.block = null;
    await zone.rendered();
    drop(zone, [new File(['x'], 'book.epub')]);
    expect(files.map((f) => f.name)).toEqual(['book.epub']);
  });

  describe('drag highlight', () => {
    /** happy-dom has no DragEvent; a plain bubbling Event stands in, as `drop` above. */
    function drag(el: Element, type: 'dragenter' | 'dragover' | 'dragleave') {
      const event = new Event(type, { bubbles: true, cancelable: true });
      el.dispatchEvent(event);
      return event;
    }
    const over = (zone: Element) => zone.classList.contains('r2m-file-drop--over');

    it('entering the zone highlights it', async () => {
      const { zone } = await mount();
      drag(zone, 'dragenter');
      expect(drag(zone, 'dragover').defaultPrevented).toBe(true);
      expect(over(zone)).toBe(true);
    });

    it('holds while the pointer moves onto a child and back', async () => {
      const { zone } = await mount();
      const label = zone.querySelector('.r2m-file-drop__label')!;
      drag(zone, 'dragenter');
      drag(zone, 'dragover');

      // Zone → label: the browser fires dragenter on the label, then dragleave on the zone.
      drag(label, 'dragenter');
      drag(zone, 'dragleave');
      expect(over(zone)).toBe(true);
      drag(label, 'dragover');

      // Label → zone: dragenter on the zone, then dragleave on the label (bubbles to the zone).
      drag(zone, 'dragenter');
      drag(label, 'dragleave');
      expect(over(zone)).toBe(true);
    });

    it('leaving the zone clears it, even from a child', async () => {
      const { zone } = await mount();
      const button = zone.querySelector('button')!;
      drag(zone, 'dragenter');
      drag(zone, 'dragover');
      drag(button, 'dragenter');
      drag(zone, 'dragleave');
      expect(over(zone)).toBe(true);

      drag(button, 'dragleave');
      expect(over(zone)).toBe(false);
    });

    it('a drop clears it and the next drag starts afresh', async () => {
      const { zone } = await mount();
      const label = zone.querySelector('.r2m-file-drop__label')!;
      drag(zone, 'dragenter');
      drag(label, 'dragenter');
      drag(zone, 'dragleave');
      drag(label, 'dragover');
      drop(label, [new File(['x'], 'book.epub')]);
      expect(over(zone)).toBe(false);

      drag(zone, 'dragenter');
      drag(zone, 'dragover');
      drag(zone, 'dragleave');
      expect(over(zone)).toBe(false);
    });

    it('a blocked zone never takes the highlight', async () => {
      const { zone } = await mount();
      zone.block = { reason: 'Viewing a past step' };
      await zone.rendered();
      drag(zone, 'dragenter');
      expect(drag(zone, 'dragover').defaultPrevented).toBe(false);
      expect(over(zone)).toBe(false);
    });

    it('a gate that closes mid-drag drops the highlight', async () => {
      const { zone } = await mount();
      drag(zone, 'dragenter');
      drag(zone, 'dragover');
      expect(over(zone)).toBe(true);

      zone.block = { reason: 'Viewing a past step' };
      await zone.rendered();
      expect(over(zone)).toBe(false);
    });
  });
});
