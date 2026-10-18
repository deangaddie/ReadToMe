import { describe, expect, it } from 'bun:test';
import { type BookShape, type TitleLevel, addedTitlesMessage, availableLevels } from './add-titles';

describe('availableLevels', () => {
  const cases: [string, BookShape, Record<TitleLevel, boolean>][] = [
    [
      'one volume, one part, chapters',
      { volumes: 1, parts: 1, chapters: 2 },
      { book: true, volume: false, part: false, chapter: true },
    ],
    [
      'a one-chapter book: the book title only',
      { volumes: 1, parts: 1, chapters: 1 },
      { book: true, volume: false, part: false, chapter: false },
    ],
    [
      'two volumes, one part each, chapters',
      { volumes: 2, parts: 2, chapters: 6 },
      { book: true, volume: true, part: false, chapter: true },
    ],
    [
      'every level',
      { volumes: 2, parts: 4, chapters: 12 },
      { book: true, volume: true, part: true, chapter: true },
    ],
  ];
  for (const [name, shape, expected] of cases) {
    it(name, () => expect(availableLevels(shape)).toEqual(expected));
  }
});

describe('addedTitlesMessage', () => {
  const cases: [TitleLevel[], string][] = [
    [['book'], 'Added book title'],
    [['chapter'], 'Added chapter titles'],
    [['book', 'chapter'], 'Added book and chapter titles'],
    [['book', 'volume', 'part'], 'Added book, volume and part titles'],
  ];
  for (const [levels, expected] of cases) {
    it(expected, () => expect(addedTitlesMessage(levels)).toBe(expected));
  }
});
