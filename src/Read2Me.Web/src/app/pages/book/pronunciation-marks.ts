import { html } from 'lit-html';
import type {
  Guid,
  ParagraphDto,
  PronunciationRef,
  PronunciationSpan,
  PronunciationSpanReport,
} from '@app/api';

/**
 * The Book view's Pronunciation marks (pronunciation ticket 15, variant D) as pure functions and
 * partials: the host's `…/matches` spans cut each item's text into plain runs and marks. The text
 * itself is never changed; a mark is a dotted underline the hover card hangs from.
 */

/** One span in an item's text, as the host's matcher found it; the item is the index key. */
export type MarkSpan = Omit<PronunciationSpan, 'itemId'>;

/** Spans per item id, in text order. */
export type MarkIndex = Readonly<Record<Guid, readonly MarkSpan[]>>;

export interface IndexedMarks {
  byItem: MarkIndex;
  /** The entries the spans name, by id. */
  entries: Readonly<Record<Guid, PronunciationRef>>;
}

export const NO_MARKS: IndexedMarks = { byItem: {}, entries: {} };

export function indexSpans(report: PronunciationSpanReport): IndexedMarks {
  const byItem: Record<Guid, MarkSpan[]> = {};
  for (const { itemId, ...span } of report.spans) (byItem[itemId] ??= []).push(span);
  for (const spans of Object.values(byItem)) spans.sort((a, b) => a.start - b.start);
  return {
    byItem,
    entries: Object.fromEntries(report.pronunciations.map((p) => [p.id, p])),
  };
}

export interface TextSegment {
  text: string;
  mark?: MarkSpan;
}

/**
 * `text[from, to)` cut around the spans that lie wholly inside it. A span outside the range or
 * past the text (spans read before an edit landed) is left out rather than cut a word apart.
 */
export function segmentsOf(
  text: string,
  spans: readonly MarkSpan[] | undefined,
  from = 0,
  to = text.length,
): TextSegment[] {
  const segments: TextSegment[] = [];
  let at = from;
  for (const mark of spans ?? []) {
    const end = mark.start + mark.length;
    if (mark.start < at || end > to || mark.length <= 0) continue;
    if (mark.start > at) segments.push({ text: text.slice(at, mark.start) });
    segments.push({ text: text.slice(mark.start, end), mark });
    at = end;
  }
  if (at < to || segments.length === 0) segments.push({ text: text.slice(at, to) });
  return segments;
}

/** An item's text with its marks, as row content; `from`/`to` as for {@link segmentsOf}. */
export function markedText(
  itemId: Guid,
  text: string,
  spans: readonly MarkSpan[] | undefined,
  from = 0,
  to = text.length,
) {
  if (!spans?.length) return text.slice(from, to);
  return segmentsOf(text, spans, from, to).map((s) =>
    s.mark
      ? html`<span
          class="r2m-pron-mark"
          tabindex="0"
          data-item-id=${itemId}
          data-pronunciation-id=${s.mark.pronunciationId}
          data-sentence-start=${s.mark.sentenceStart}
          >${s.text}</span
        >`
      : s.text,
  );
}

/** Read mode's prose ({@link paragraphText}'s joined, trimmed items), each item's marks in place. */
export function proseOf(paragraph: ParagraphDto, marks: MarkIndex) {
  const parts = paragraph.items.filter((i) => !i.isPause && i.text);
  return parts.map((item, n) => {
    const text = item.text!;
    const from = text.length - text.trimStart().length;
    const to = text.trimEnd().length;
    return html`${n > 0 ? ' ' : ''}${markedText(item.id, text, marks[item.id], from, to)}`;
  });
}
