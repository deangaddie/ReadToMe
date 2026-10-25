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

const span = (start: number, length: number, pronunciationId = 'holmes', sentenceStart = 0) =>
  ({ start, length, pronunciationId, sentenceStart }) satisfies MarkSpan;

const report: PronunciationSpanReport = {
  pronunciations: [
    { id: 'wat', writtenForm: 'Watson', spokenForm: 'WAT-sun' },
    { id: 'holmes', writtenForm: 'Holmes', spokenForm: 'HOHMZ' },
  ],
  spans: [
    {
      itemId: 'i1',
      start: 0,
      length: 6,
      pronunciationId: 'holmes',
      sentenceStart: 0,
    },
    {
      itemId: 'i2',
      start: 4,
      length: 6,
      pronunciationId: 'wat',
      sentenceStart: 0,
    },
    {
      itemId: 'i1',
      start: 12,
      length: 6,
      pronunciationId: 'holmes',
      sentenceStart: 12,
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
    expect(byItem['i1']).toEqual([span(0, 6), span(12, 6, 'holmes', 12)]);
    expect(byItem['i2']).toEqual([span(4, 6, 'wat')]);
    expect(entries['wat']?.spokenForm).toBe('WAT-sun');
  });
});

describe('segmentsOf', () => {
  it('cuts the text around each span, keeping every character', () => {
    const segments = segmentsOf('Holmes ran. Holmes fell.', [
      span(0, 6),
      span(12, 6, 'holmes', 12),
    ]);
    expect(segments.map((s) => [s.text, s.mark?.start ?? null])).toEqual([
      ['Holmes', 0],
      [' ran. ', null],
      ['Holmes', 12],
      [' fell.', null],
    ]);
  });

  it('is the text alone without spans', () => {
    expect(segmentsOf('No marks.', [])).toEqual([{ text: 'No marks.' }]);
  });

  it('keeps to the range and drops a span outside it or past the text', () => {
    const segments = segmentsOf('  Holmes ran.  ', [span(2, 6), span(20, 3)], 2, 13);
    expect(segments.map((s) => [s.text, !!s.mark])).toEqual([
      ['Holmes', true],
      [' ran.', false],
    ]);
  });
});

describe('markedText', () => {
  it('underlines each span as a focusable mark and leaves the text unchanged', () => {
    const el = host(
      markedText('i1', 'Holmes ran. Holmes fell.', [span(0, 6), span(12, 6, 'holmes', 12)]),
    );
    expect(el.textContent).toBe('Holmes ran. Holmes fell.');
    const marks = [...el.querySelectorAll<HTMLElement>('.r2m-pron-mark')];
    expect(marks.map((m) => m.textContent)).toEqual(['Holmes', 'Holmes']);
    expect(marks[1]!.getAttribute('tabindex')).toBe('0');
    expect(marks[1]!.dataset['itemId']).toBe('i1');
    expect(marks[1]!.dataset['pronunciationId']).toBe('holmes');
    expect(marks[1]!.dataset['sentenceStart']).toBe('12');
  });

  it('says where each mark starts in the item text, and lights the marks of the entries named', () => {
    const el = host(
      markedText('i1', 'Holmes met Watson.', [span(0, 6), span(11, 6, 'wat')], {
        lit: new Set(['wat']),
      }),
    );
    const marks = [...el.querySelectorAll<HTMLElement>('.r2m-pron-mark')];
    expect(marks.map((m) => m.dataset['start'])).toEqual(['0', '11']);
    expect(marks.map((m) => m.classList.contains('r2m-pron-mark--lit'))).toEqual([false, true]);
  });

  it('is plain text for an item without spans', () => {
    const el = host(markedText('i1', 'Holmes ran.', undefined));
    expect(el.textContent).toBe('Holmes ran.');
    expect(el.querySelector('.r2m-pron-mark')).toBeNull();
  });
});

describe('proseOf', () => {
  it("reads as Read mode's joined prose, each item's marks in place", () => {
    const paragraph = {
      id: 'p1',
      items: [
        item('i1', '  Holmes ran. '),
        item('gap', null, { isPause: true }),
        item('i2', 'Then Watson came.'),
      ],
    } as unknown as ParagraphDto;
    const el = host(proseOf(paragraph, { i1: [span(2, 6)], i2: [span(5, 6, 'wat')] }));
    expect(el.textContent).toBe(paragraphText(paragraph));
    expect([...el.querySelectorAll('.r2m-pron-mark')].map((m) => m.textContent)).toEqual([
      'Holmes',
      'Watson',
    ]);
  });

  it("holds each item's text in its own run, saying where in the item it starts", () => {
    const paragraph = {
      id: 'p1',
      items: [item('i1', '  Holmes ran. '), item('i2', 'Then Watson came.')],
    } as unknown as ParagraphDto;
    const el = host(proseOf(paragraph, {}));
    const runs = [...el.querySelectorAll<HTMLElement>('[data-item-text]')];
    expect(runs.map((r) => [r.dataset['itemText'], r.dataset['textFrom'], r.textContent])).toEqual([
      ['i1', '2', 'Holmes ran.'],
      ['i2', '0', 'Then Watson came.'],
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
    items: [item('i1', '  Holmes ran. '), item('i2', 'Then Watson came.')],
  } as unknown as ParagraphDto;

  it('is the item, the offset in its text where the selection starts, and the selected text', () => {
    const el = host(proseOf(paragraph, {}));
    expect(selectedPlace(rangeOver(el, 'Watson'))).toEqual({
      itemId: 'i2',
      offset: 5,
      text: 'Watson',
    });
    expect(selectedPlace(rangeOver(el, 'ran'))).toEqual({
      itemId: 'i1',
      offset: 9,
      text: 'ran',
    });
    el.remove();
  });

  it('counts the text of the marks before it', () => {
    const el = host(
      html`<span data-item-text="i1"
        >${markedText('i1', 'Holmes met Watson.', [span(0, 6)])}</span
      >`,
    );
    expect(selectedPlace(rangeOver(el, 'Watson'))).toEqual({
      itemId: 'i1',
      offset: 11,
      text: 'Watson',
    });
    el.remove();
  });

  it('is none for a selection across two items, outside any item, or empty', () => {
    const el = host(proseOf(paragraph, {}));
    expect(selectedPlace(rangeOver(el, 'ran', 'Then'))).toBeNull();
    const collapsed = rangeOver(el, 'Watson');
    collapsed.collapse(true);
    expect(selectedPlace(collapsed)).toBeNull();
    el.remove();
    const outside = host(html`<p>Nothing here</p>`);
    expect(selectedPlace(rangeOver(outside, 'Nothing'))).toBeNull();
    outside.remove();
  });
});
