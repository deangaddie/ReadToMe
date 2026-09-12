import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FileDrop, RejectedFile } from './file-drop';

@Component({
  imports: [FileDrop],
  template: `
    <r2m-file-drop
      accept=".epub,audio/*"
      [maxBytes]="100"
      label="Drop a book"
      (files)="files.push(...$event)"
      (rejected)="rejected.push(...$event)"
    />
  `,
})
class HostCmp {
  files: File[] = [];
  rejected: RejectedFile[] = [];
}

/** jsdom has no DataTransfer/DragEvent; a plain Event with a dataTransfer-shaped payload is enough. */
function drop(el: Element, files: File[]) {
  const event = new Event('drop', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'dataTransfer', { value: { files } });
  el.dispatchEvent(event);
}

describe('r2m-file-drop', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('renders the label and a picker button', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.r2m-file-drop__label')?.textContent).toBe('Drop a book');
    expect(el.querySelector('button')?.textContent?.trim()).toBe('Choose file');
    expect(el.querySelector<HTMLInputElement>('input[type=file]')?.accept).toBe('.epub,audio/*');
  });

  it('accepts matching files and rejects wrong type or oversize', async () => {
    const fixture = await mount();
    const zone = (fixture.nativeElement as HTMLElement).querySelector('r2m-file-drop')!;

    drop(zone, [new File(['x'], 'book.EPUB')]);
    drop(zone, [new File(['x'], 'clip.wav', { type: 'audio/wav' })]);
    drop(zone, [new File(['x'], 'notes.txt', { type: 'text/plain' })]);
    drop(zone, [new File(['x'.repeat(101)], 'big.epub')]);

    expect(fixture.componentInstance.files.map((f) => f.name)).toEqual(['book.EPUB', 'clip.wav']);
    expect(fixture.componentInstance.rejected.map((r) => [r.file.name, r.reason])).toEqual([
      ['notes.txt', 'type'],
      ['big.epub', 'size'],
    ]);
  });

  it('keeps only the first file when not multiple', async () => {
    const fixture = await mount();
    const zone = (fixture.nativeElement as HTMLElement).querySelector('r2m-file-drop')!;

    drop(zone, [new File(['a'], 'a.epub'), new File(['b'], 'b.epub')]);

    expect(fixture.componentInstance.files.map((f) => f.name)).toEqual(['a.epub']);
  });
});
