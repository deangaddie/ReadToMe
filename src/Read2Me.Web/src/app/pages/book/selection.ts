import { NodeLevel, ParagraphDto, ParagraphItemDto } from '@app/api';
import { ChapterParents } from './book-tree';
import { LoadedChapterView } from './reader-rows';

/**
 * The reader selections' pure rules (design §6.3): which paragraphs (ticket 12) and which items
 * (ticket 13) can be selected, how a selection rolls up into a tree node's tri-state, and how
 * many a node holds. The `SelectionStore` / `AudioSelectionStore` keep the state; everything
 * decidable from the data is decided here and table-tested once.
 */

/** What a node read answers per row: the row's id and the nodes it rolls up into. */
export interface SelectionRef {
  id: string;
  chapterId: string;
  partId: string;
  volumeId: string;
}

/** The nodes a selected row rolls up into; part and volume may be unknown for a row ticked
 * before its structure loaded, in which case it counts under no part or volume. */
export interface ParagraphAncestry {
  chapterId: string;
  partId: ChapterParents['partId'] | null;
  volumeId: ChapterParents['volumeId'] | null;
}

export type SelectionMap = Readonly<Record<string, ParagraphAncestry>>;

export type TriState = 'unchecked' | 'indeterminate' | 'checked';

/**
 * A Character paragraph: holds at least one dialog line. Like `isNarration` in `reader-rows.ts`,
 * this reads the host's `itemType` word rather than comparing speaker ids (ADR-0006: the host
 * decides what is narration).
 */
export function isDialogParagraph(paragraph: ParagraphDto): boolean {
  return paragraph.items.some((i) => !i.isPause && i.itemType === 'Character');
}

/**
 * A voiced item (CONTEXT.md "Voiced item", the host's `voicedOnly` rule): a spoken item with
 * somebody to read it — any speaker at all (narration carries the narrator, ADR-0006), or any
 * line in narrator-only mode. Existing audio is no obstacle: a take can be regenerated. What the
 * audio selection can hold; the subset still missing a WAV is the glossary's Generatable item.
 */
export function isVoicedItem(item: ParagraphItemDto, narratorOnlyMode: boolean): boolean {
  if (item.isPause) return false;
  return item.characterId !== null || narratorOnlyMode;
}

/** The ancestry a ticked row records, from what the reader knows of its chapter so far. */
export function ancestryFor(
  ancestry: Readonly<Record<string, ChapterParents>>,
  chapterId: string,
): ParagraphAncestry {
  const parents = ancestry[chapterId];
  return { chapterId, partId: parents?.partId ?? null, volumeId: parents?.volumeId ?? null };
}

export function selectedUnder(selection: SelectionMap, level: NodeLevel, nodeId: string): number {
  let n = 0;
  for (const a of Object.values(selection)) {
    const under =
      level === 'volume' ? a.volumeId : level === 'part' ? a.partId : a.chapterId;
    if (under === nodeId) n++;
  }
  return n;
}

/**
 * A node's checkbox (CONTEXT.md "Roll-up"): nothing selected under it is unchecked; everything it
 * holds is checked; anything else — including a node whose total is not known yet — is
 * indeterminate, so a partly known selection never claims to be complete.
 */
export function nodeState(
  selection: SelectionMap,
  level: NodeLevel,
  nodeId: string,
  total: number | undefined,
): TriState {
  const selected = selectedUnder(selection, level, nodeId);
  if (selected === 0) return 'unchecked';
  return total !== undefined && total > 0 && selected >= total ? 'checked' : 'indeterminate';
}

/** How many Character paragraphs each loaded chapter holds — the chapter totals the tree can know without a read. */
export function chapterDialogTotals(chapters: readonly LoadedChapterView[]): Record<string, number> {
  const totals: Record<string, number> = {};
  for (const chapter of chapters) {
    totals[chapter.id] = chapter.paragraphs.filter(isDialogParagraph).length;
  }
  return totals;
}

/** How many voiced items each loaded chapter holds — the audio totals the tree can know without a read. */
export function chapterVoicedTotals(
  chapters: readonly LoadedChapterView[],
  narratorOnlyMode: boolean,
): Record<string, number> {
  const totals: Record<string, number> = {};
  for (const chapter of chapters) {
    let n = 0;
    for (const paragraph of chapter.paragraphs) {
      for (const item of paragraph.items) if (isVoicedItem(item, narratorOnlyMode)) n++;
    }
    totals[chapter.id] = n;
  }
  return totals;
}

export function ancestryOf(ref: SelectionRef): ParagraphAncestry {
  return { chapterId: ref.chapterId, partId: ref.partId, volumeId: ref.volumeId };
}
