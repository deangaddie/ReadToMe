import { afterEach, describe, expect, it } from 'bun:test';
import type { ProjectSummary } from '@app/api';
import { type SeriesView, membershipOf } from './series-shelf';
import type { BookPicker, SeriesPicker } from './series-pickers';
import './series-pickers';

const book = (folderName: string, title: string, author: string | null = null) =>
  ({ folderName, title, author }) as ProjectSummary;

const scarlet = book('scarlet', 'A Study in Scarlet');
const four = book('four', 'The Sign of the Four');
const oz = book('oz', 'The Wonderful Wizard of Oz');
const emma = book('emma', 'Emma', 'Jane Austen');
const zenda = book('zenda', 'The Prisoner of Zenda');

const holmes: SeriesView = {
  id: 'h',
  name: 'Sherlock Holmes',
  members: [
    { project: scarlet, position: 1 },
    { project: four, position: 2.5 },
  ],
};
const ozBooks: SeriesView = { id: 'o', name: 'Oz', members: [{ project: oz, position: 1 }] };
const austen: SeriesView = { id: 'a', name: 'Austen', members: [] };

afterEach(() => document.body.replaceChildren());

async function closed<T>(el: HTMLElement, click: string): Promise<T> {
  const result = new Promise<T>((resolve) =>
    el.addEventListener('r2m-close', (e) => resolve((e as CustomEvent<T>).detail), { once: true }),
  );
  el.querySelector<HTMLButtonElement>(click)!.click();
  return result;
}

/** Text with its whitespace collapsed. */
const text = (el: Element | null) => el!.textContent!.replace(/\s+/g, ' ').trim();

/** A row's name and meta, without the icon's ligature text. */
const rowText = (el: HTMLElement, selector: string) =>
  Array.from(el.querySelectorAll(selector), (r) =>
    Array.from(r.querySelectorAll('span:not(.r2m-icon)'), text).join(' '),
  );

describe('r2m-series-picker', () => {
  async function mount(folder: string, title: string) {
    const el = document.createElement('r2m-series-picker') as SeriesPicker;
    const series = [austen, ozBooks, holmes];
    el.data = { book: { folder, title }, series, membership: membershipOf(series) };
    document.body.append(el);
    await el.rendered();
    return el;
  }

  it('lists each series with the number the book would join as, then New series', async () => {
    const el = await mount('emma', 'Emma');
    expect(el.querySelector('.r2m-dialog__title')?.textContent).toBe('Add Emma to a series');
    expect(rowText(el, '.series-picker__row')).toEqual([
      'Austen No books yet · joins as Book 1',
      'Oz 1 book · joins as Book 2',
      'Sherlock Holmes 2 books · joins as Book 3',
      'New series… this book becomes Book 1',
    ]);
    expect(el.querySelector('.series-picker__current')).toBeNull();
  });

  it('leaves out the book’s own series and says moving asks first', async () => {
    const el = await mount('four', 'The Sign of the Four');
    expect(text(el.querySelector('.series-picker__current'))).toBe(
      'Now Book 2.5 of Sherlock Holmes. Moving it asks first.',
    );
    expect(
      Array.from(el.querySelectorAll('[data-series-id]'), (r) => r.getAttribute('data-series-id')),
    ).toEqual(['a', 'o']);
  });

  it('closes with the picked series, or New series', async () => {
    const el = await mount('emma', 'Emma');
    expect(await closed(el, '[data-series-id="o"]')).toEqual({ seriesId: 'o' });
    expect(await closed(el, '[data-action="new-series"]')).toEqual({ newSeries: true });
    expect(await closed(el, '[data-action="cancel"]')).toBeNull();
  });
});

describe('r2m-book-picker', () => {
  async function mount(target: SeriesView, projects: ProjectSummary[]) {
    const el = document.createElement('r2m-book-picker') as BookPicker;
    el.data = { target, projects, membership: membershipOf([holmes, ozBooks, austen]) };
    document.body.append(el);
    await el.rendered();
    return el;
  }

  it('groups the other books into Not in a series and In another series', async () => {
    const el = await mount(ozBooks, [emma, four, oz, scarlet, zenda]);
    expect(el.querySelector('.r2m-dialog__title')?.textContent).toBe('Add a book to Oz');
    expect(text(el.querySelector('.book-picker__note'))).toBe(
      'It joins as Book 2; edit the number afterwards if it belongs elsewhere.',
    );
    expect(Array.from(el.querySelectorAll('.book-picker__group'), text)).toEqual([
      'Not in a series',
      'In another series',
    ]);
    expect(rowText(el, '.series-picker__row')).toEqual([
      'Emma Jane Austen',
      'The Prisoner of Zenda',
      'A Study in Scarlet Book 1 of Sherlock Holmes · moving asks first',
      'The Sign of the Four Book 2.5 of Sherlock Holmes · moving asks first',
    ]);
  });

  it('closes with the picked folder', async () => {
    const el = await mount(austen, [emma, scarlet]);
    expect(await closed(el, '[data-folder="scarlet"]')).toBe('scarlet');
  });

  it('says so when every book is already in the series', async () => {
    const el = await mount(ozBooks, [oz]);
    expect(text(el.querySelector('.book-picker__empty'))).toBe(
      'Every book in the workspace is already in Oz.',
    );
  });
});
