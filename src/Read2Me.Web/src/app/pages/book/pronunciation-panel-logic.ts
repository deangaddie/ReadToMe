import type { Guid, PronunciationEntry, PronunciationRef } from '@app/api';
import { type Forms, WRITTEN_FORM_MAX } from '../pronunciations/pronunciation-logic';
import type { TextPlace } from '../pronunciations/try-out-logic';
import type { IndexedMarks } from './pronunciation-marks';

/**
 * The Book view's Pronunciations panel (pronunciation ticket 16, variant D) as pure functions of
 * what the host answered: the rows of the entries in the loaded chapters and the Add / Edit pill a
 * selection shows.
 */

/** One entry occurring in the loaded chapters. */
export interface PanelRow {
  entry: PronunciationRef;
  /** Its matches in the loaded chapters. */
  count: number;
  /** The loaded items holding a match that have audio. */
  withAudio: number;
  /** Its first match there: where a click on the row opens the editor from. */
  first: TextPlace;
}

/**
 * One row per entry the loaded chapters' spans name, in order of each entry's first match. The
 * index keys its items in the order the host's report listed them (book order), so walking it
 * walks the book.
 */
export function panelRows(marks: IndexedMarks, hasAudio: (itemId: Guid) => boolean): PanelRow[] {
  const rows = new Map<Guid, PanelRow>();
  for (const [itemId, spans] of Object.entries(marks.byItem)) {
    // An item counts once towards each entry it holds, however often it matches there.
    const counted = new Set<Guid>();
    for (const span of spans) {
      const entry = marks.entries[span.pronunciationId];
      if (!entry) continue;
      let row = rows.get(entry.id);
      if (!row) {
        row = { entry, count: 0, withAudio: 0, first: { itemId, offset: span.start } };
        rows.set(entry.id, row);
      }
      row.count++;
      if (!counted.has(entry.id)) {
        counted.add(entry.id);
        if (hasAudio(itemId)) row.withAudio++;
      }
    }
  }
  return [...rows.values()];
}

/** The longest selection the pill offers, in words. */
export const PILL_MAX_WORDS = 4;

/** What a selection in an item's text offers: add it, or edit the entry it already is. */
export interface SelectionPill {
  action: 'add' | 'edit';
  /** The entry whose written form the selection is, ignoring case; null to add. */
  entry: PronunciationEntry | null;
  /** What the editor opens with: the selection twice to add, the entry's own forms to edit. */
  forms: Forms;
  /** Characters dropped from the selection's front: its text starts this far into the selection. */
  lead: number;
}

/** Letters and digits are word characters; anything else at an end is what a drag caught. */
const EDGE = /^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu;

/**
 * The pill a selection shows: 1–{@link PILL_MAX_WORDS} words once the space and punctuation at its
 * ends are dropped (an inner apostrophe stays), or null. Inner whitespace runs read as one space,
 * as the host stores a form.
 */
export function selectionPill(
  selected: string,
  entries: readonly PronunciationEntry[],
): SelectionPill | null {
  const lead = selected.length - selected.replace(/^[^\p{L}\p{N}]+/u, '').length;
  const text = selected.replace(EDGE, '').replace(/\s+/g, ' ');
  if (text === '' || text.length > WRITTEN_FORM_MAX) return null;
  if (text.split(' ').length > PILL_MAX_WORDS) return null;
  const key = text.toLowerCase();
  const entry = entries.find((e) => e.writtenForm.toLowerCase() === key) ?? null;
  return entry
    ? {
        action: 'edit',
        entry,
        forms: { writtenForm: entry.writtenForm, spokenForm: entry.spokenForm },
        lead,
      }
    : {
        action: 'add',
        entry: null,
        forms: { writtenForm: text, spokenForm: text },
        lead,
      };
}
