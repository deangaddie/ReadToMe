import { describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { ParagraphDto, ParagraphItemDto, PronunciationSpanReport } from '@app/api';
import { type MarkSpan, indexSpans, markedText, proseOf, segmentsOf } from './pronunciation-marks';
import { paragraphText } from './reader-rows';

const span = (start: number, length: number, pronunciationId = 'pug', sentenceStart = 0) =>
  ({ start, length, pronunciationId, sentenceStart }) satisfies MarkSpan;

const report: PronunciationSpanReport = {
  pronunciations: [
    { id: 'kul', writtenForm: 'Kulgan', spokenForm: 'KULL-gun' },
    { id: 'pug', writtenForm: 'Pug', spokenForm: 'PUHG' },
  ],
  spans: [
    {
      itemId: 'i1',
      start: 0,
      length: 3,
      pronunciationId: 'pug',
      sentenceStart: 0,
    },
    {
      itemId: 'i2',
      start: 4,
      length: 6,
      pronunciationId: 'kul',
      sentenceStart: 0,
    },
    {
      itemId: 'i1',
      start: 9,
      length: 3,
      pronunciationId: 'pug',
      sentenceStart: 9,
    },
  ],
};

function host(template: unknown): HTMLElement {
  const el = document.createElement('div');
  render(template, el);
  return el;
}

const item = (id: string, text: string | null, flags: Partial<ParagraphItemDto> = {}) =>
  ({
    id,
    text,
    isPause: false,
    itemType: 'Narration',
    ...flags,
  }) as ParagraphItemDto;

describe('indexSpans', () => {
  it('groups the spans by item in order and keys the entries by id', () => {
    const { byItem, entries } = indexSpans(report);
    expect(byItem['i1']).toEqual([span(0, 3), span(9, 3, 'pug', 9)]);
    expect(byItem['i2']).toEqual([span(4, 6, 'kul')]);
    expect(entries['kul']?.spokenForm).toBe('KULL-gun');
  });
});

describe('segmentsOf', () => {
  it('cuts the text around each span, keeping every character', () => {
    const segments = segmentsOf('Pug ran. Pug fell.', [span(0, 3), span(9, 3, 'pug', 9)]);
    expect(segments.map((s) => [s.text, s.mark?.start ?? null])).toEqual([
      ['Pug', 0],
      [' ran. ', null],
      ['Pug', 9],
      [' fell.', null],
    ]);
  });

  it('is the text alone without spans', () => {
    expect(segmentsOf('No marks.', [])).toEqual([{ text: 'No marks.' }]);
  });

  it('keeps to the range and drops a span outside it or past the text', () => {
    const segments = segmentsOf('  Pug ran.  ', [span(2, 3), span(20, 3)], 2, 10);
    expect(segments.map((s) => [s.text, !!s.mark])).toEqual([
      ['Pug', true],
      [' ran.', false],
    ]);
  });
});

describe('markedText', () => {
  it('underlines each span as a focusable mark and leaves the text unchanged', () => {
    const el = host(markedText('i1', 'Pug ran. Pug fell.', [span(0, 3), span(9, 3, 'pug', 9)]));
    expect(el.textContent).toBe('Pug ran. Pug fell.');
    const marks = [...el.querySelectorAll<HTMLElement>('.r2m-pron-mark')];
    expect(marks.map((m) => m.textContent)).toEqual(['Pug', 'Pug']);
    expect(marks[1]!.getAttribute('tabindex')).toBe('0');
    expect(marks[1]!.dataset['itemId']).toBe('i1');
    expect(marks[1]!.dataset['pronunciationId']).toBe('pug');
    expect(marks[1]!.dataset['sentenceStart']).toBe('9');
  });

  it('is plain text for an item without spans', () => {
    const el = host(markedText('i1', 'Pug ran.', undefined));
    expect(el.textContent).toBe('Pug ran.');
    expect(el.querySelector('.r2m-pron-mark')).toBeNull();
  });
});

describe('proseOf', () => {
  it("reads as Read mode's joined prose, each item's marks in place", () => {
    const paragraph = {
      id: 'p1',
      items: [
        item('i1', '  Pug ran. '),
        item('gap', null, { isPause: true }),
        item('i2', 'Then Kulgan came.'),
      ],
    } as unknown as ParagraphDto;
    const el = host(proseOf(paragraph, { i1: [span(2, 3)], i2: [span(5, 6, 'kul')] }));
    expect(el.textContent).toBe(paragraphText(paragraph));
    expect([...el.querySelectorAll('.r2m-pron-mark')].map((m) => m.textContent)).toEqual([
      'Pug',
      'Kulgan',
    ]);
  });
});
