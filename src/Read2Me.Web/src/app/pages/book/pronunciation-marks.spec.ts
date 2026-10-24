import { describe, expect, it } from 'bun:test';
import { html, render } from 'lit-html';
import type { ParagraphDto, ParagraphItemDto, PronunciationSpanReport } from '@app/api';
import {
  type MarkSpan,
  indexSpans,
  markedText,
  proseOf,
  segmentsOf,
  selectedPlace,
} from './pronunciation-marks';
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

  it('says where each mark starts in the item text, and lights the marks of the entries named', () => {
    const el = host(
      markedText('i1', 'Pug met Kulgan.', [span(0, 3), span(8, 6, 'kul')], {
        lit: new Set(['kul']),
      }),
    );
    const marks = [...el.querySelectorAll<HTMLElement>('.r2m-pron-mark')];
    expect(marks.map((m) => m.dataset['start'])).toEqual(['0', '8']);
    expect(marks.map((m) => m.classList.contains('r2m-pron-mark--lit'))).toEqual([false, true]);
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

  it("holds each item's text in its own run, saying where in the item it starts", () => {
    const paragraph = {
      id: 'p1',
      items: [item('i1', '  Pug ran. '), item('i2', 'Then Kulgan came.')],
    } as unknown as ParagraphDto;
    const el = host(proseOf(paragraph, {}));
    const runs = [...el.querySelectorAll<HTMLElement>('[data-item-text]')];
    expect(runs.map((r) => [r.dataset['itemText'], r.dataset['textFrom'], r.textContent])).toEqual([
      ['i1', '2', 'Pug ran.'],
      ['i2', '0', 'Then Kulgan came.'],
    ]);
  });
});

/** The text node under `root` holding `text`. */
function textNode(root: Node, text: string): Text {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    if (n.textContent?.includes(text)) return n as Text;
  }
  throw new Error(`no text node holds “${text}”`);
}

/** A range over `text` (in one text node under `root`), `root` attached to the document. */
function rangeOver(root: HTMLElement, text: string, upTo?: string): Range {
  document.body.append(root);
  const start = textNode(root, text);
  const end = upTo === undefined ? start : textNode(root, upTo);
  const range = document.createRange();
  range.setStart(start, start.data.indexOf(text));
  range.setEnd(
    end,
    upTo === undefined
      ? start.data.indexOf(text) + text.length
      : end.data.indexOf(upTo) + upTo.length,
  );
  return range;
}

describe('selectedPlace', () => {
  const paragraph = {
    id: 'p1',
    items: [item('i1', '  Pug ran. '), item('i2', 'Then Kulgan came.')],
  } as unknown as ParagraphDto;

  it('is the item, the offset in its text where the selection starts, and the selected text', () => {
    const el = host(proseOf(paragraph, {}));
    expect(selectedPlace(rangeOver(el, 'Kulgan'))).toEqual({
      itemId: 'i2',
      offset: 5,
      text: 'Kulgan',
    });
    expect(selectedPlace(rangeOver(el, 'ran'))).toEqual({
      itemId: 'i1',
      offset: 6,
      text: 'ran',
    });
    el.remove();
  });

  it('counts the text of the marks before it', () => {
    const el = host(
      html`<span data-item-text="i1">${markedText('i1', 'Pug met Kulgan.', [span(0, 3)])}</span>`,
    );
    expect(selectedPlace(rangeOver(el, 'Kulgan'))).toEqual({
      itemId: 'i1',
      offset: 8,
      text: 'Kulgan',
    });
    el.remove();
  });

  it('is none for a selection across two items, outside any item, or empty', () => {
    const el = host(proseOf(paragraph, {}));
    expect(selectedPlace(rangeOver(el, 'ran', 'Then'))).toBeNull();
    const collapsed = rangeOver(el, 'Kulgan');
    collapsed.collapse(true);
    expect(selectedPlace(collapsed)).toBeNull();
    el.remove();
    const outside = host(html`<p>Nothing here</p>`);
    expect(selectedPlace(rangeOver(outside, 'Nothing'))).toBeNull();
    outside.remove();
  });
});
