import { describe, expect, it } from 'bun:test';
import type { ProjectSummary, SeriesDto } from '@app/api';
import {
  bookLabel,
  formatPosition,
  membershipOf,
  nextPosition,
  panelAfter,
  seriesSummary,
  seriesViews,
  shelfItems,
} from './series-shelf';

const book = (folderName: string, title: string): ProjectSummary => ({
  folderName,
  title,
  author: 'Arthur Conan Doyle',
  coverImage: null,
  audioItemTotal: 0,
  audioItemDone: 0,
  audioPercent: 0,
  series: null,
  fileType: 'Epub',
});

const scarlet = book('scarlet', 'A Study in Scarlet');
const four = book('four', 'The Sign of the Four');
const adventures = book('adventures', 'The Adventures of Sherlock Holmes');
const emma = book('emma', 'Emma');
const zenda = book('zenda', 'The Prisoner of Zenda');
const oz = book('oz', 'The Wonderful Wizard of Oz');

const holmes: SeriesDto = {
  id: 'h',
  name: 'Sherlock Holmes',
  books: [
    { folder: 'scarlet', position: 1 },
    { folder: 'four', position: 2 },
    { folder: 'adventures', position: 2.5 },
  ],
};
const ozSeries: SeriesDto = { id: 'o', name: 'oz books', books: [{ folder: 'oz', position: 1 }] };
const empty: SeriesDto = { id: 'e', name: 'Austen', books: [] };

describe('seriesViews()', () => {
  it('joins each series to its projects in position order, dropping a missing folder', () => {
    const views = seriesViews(
      [{ ...holmes, books: [...holmes.books, { folder: 'gone', position: 3 }].reverse() }],
      [scarlet, four, adventures],
    );
    expect(views[0]!.members.map((m) => [m.project.folderName, m.position])).toEqual([
      ['scarlet', 1],
      ['four', 2],
      ['adventures', 2.5],
    ]);
  });
});

describe('shelfItems()', () => {
  it('puts series cards among the books not in a series, sorted by name ignoring case', () => {
    const projects = [scarlet, four, adventures, emma, zenda, oz];
    const items = shelfItems(seriesViews([holmes, ozSeries, empty], projects), projects);
    expect(items.map((i) => i.key)).toEqual([
      'series:e', // Austen
      'book:emma', // Emma
      'series:o', // oz books
      'series:h', // Sherlock Holmes
      'book:zenda', // The Prisoner of Zenda
    ]);
  });
});

describe('membershipOf()', () => {
  it('maps each member folder to its series and position', () => {
    const m = membershipOf(seriesViews([holmes], [scarlet, four, adventures]));
    expect(m.get('four')).toEqual({ seriesId: 'h', name: 'Sherlock Holmes', position: 2 });
    expect(m.get('emma')).toBeUndefined();
  });
});

describe('panelAfter()', () => {
  it('opens the panel after the last card in the clicked card’s row', () => {
    // 4 columns, 10 items: rows are 0-3, 4-7, 8-9.
    expect(panelAfter(10, 0, 4)).toBe(3);
    expect(panelAfter(10, 3, 4)).toBe(3);
    expect(panelAfter(10, 5, 4)).toBe(7);
    expect(panelAfter(10, 8, 4)).toBe(9);
    expect(panelAfter(10, 2, 1)).toBe(2);
  });

  it('is -1 when nothing is open', () => {
    expect(panelAfter(10, -1, 4)).toBe(-1);
  });
});

describe('labels', () => {
  it('shows a position without trailing zeros', () => {
    expect(formatPosition(2)).toBe('2');
    expect(formatPosition(1.5)).toBe('1.5');
    expect(formatPosition(1.25)).toBe('1.25');
    expect(bookLabel(0)).toBe('Book 0');
  });

  it('summarises a series by count and range', () => {
    const [h] = seriesViews([holmes], [scarlet, four, adventures]);
    expect(seriesSummary(h!)).toBe('3 books · Books 1–2.5');
    const [o] = seriesViews([ozSeries], [oz]);
    expect(seriesSummary(o!)).toBe('1 book · Book 1');
    const [e] = seriesViews([empty], []);
    expect(seriesSummary(e!)).toBe('No books yet');
  });

  it('gives the next whole number after the highest, 1 for an empty series', () => {
    const [h] = seriesViews([holmes], [scarlet, four, adventures]);
    expect(nextPosition(h!)).toBe(3);
    const [e] = seriesViews([empty], []);
    expect(nextPosition(e!)).toBe(1);
  });
});
