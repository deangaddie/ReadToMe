import type { Guid, MergeDirection, ParagraphDto, ParagraphItemDto } from '@app/api';
import type { PermanentChange } from '@app/ui/dialogs';
import { type NodeMenuTarget, WORD, displayName } from './node-menu-entries';

/**
 * The Book page's permanent-change confirms (guidance-primitives spec §7): one pure builder per
 * action, returning what the confirm says or null when this particular call loses nothing. Red
 * (`destructive`) when the scope deletes, discards or replaces something the user made or waited
 * for. The counts come from the rows the action will touch; the page gathers them.
 */

function plural(n: number, word: string): string {
  return `${n} ${word}${n === 1 ? '' : 's'}`;
}

/** Add pauses, from the Book overflow, the Overview row and the pill: always asks, nothing is lost. */
export function addPausesChange(chapterCount: number): PermanentChange {
  return {
    action: 'Add pauses',
    scope: `Inserts a pause between paragraphs and at the end of every chapter (${plural(chapterCount, 'chapter')}).`,
    cost: 'Removing them later means deleting each one.',
    destructive: false,
  };
}

/**
 * Merge with previous/next at every level. The host keeps the earlier sibling: Previous folds this
 * node into the one before it, Next folds the one after into this node. An item merge also drops
 * the folded item's speaker and audio, so it is red; a node merge loses nothing.
 */
export function mergeChange(target: NodeMenuTarget, direction: MergeDirection): PermanentChange {
  const name = displayName(target);
  const kind = WORD[target.kind];
  const folds =
    direction === 'Previous'
      ? `Folds ${quote(name)} into the previous ${kind}.`
      : `Folds the next ${kind} into ${quote(name)}.`;
  const isItem = target.kind === 'item';
  const loser = direction === 'Previous' ? 'This' : 'The next';
  return {
    action: direction === 'Previous' ? 'Merge with previous' : 'Merge with next',
    scope: isItem ? `${folds} ${loser} item’s speaker and audio are discarded.` : folds,
    cost: 'Splitting it back needs the exact point.',
    destructive: isItem,
  };
}

/** A node named by its kind ("this chapter") reads unquoted; a title or text is quoted. */
function quote(name: string): string {
  return name.startsWith('this ') ? name : `“${name}”`;
}

/**
 * The selection bar's Attribute: asks only when a selected paragraph already holds an assigned
 * dialog speaker, since attribution writes the speakers afresh. The queue's write leaves audio
 * alone and never touches narration (ADR-0006), so neither counts: the spec table's audio clause
 * was wrong against the host (ticket 13).
 */
export function attributeSelectionChange(
  paragraphs: readonly ParagraphDto[],
): PermanentChange | null {
  let speakers = 0;
  for (const paragraph of paragraphs) {
    for (const item of paragraph.items) {
      if (isDialog(item) && item.characterId !== null) speakers++;
    }
  }
  if (speakers === 0) return null;
  return {
    action: 'Attribute',
    scope:
      `Re-attributes ${plural(paragraphs.length, 'paragraph')}: ` +
      `${plural(speakers, 'assigned speaker')} ${speakers === 1 ? 'is' : 'are'} replaced.`,
    cost: 'Getting a replaced speaker back means assigning it again by hand.',
    destructive: true,
  };
}

/** A dialog item: spoken, and not narration. Reads the host's `itemType` word (ADR-0006). */
function isDialog(item: ParagraphItemDto): boolean {
  return !item.isPause && item.itemType === 'Character';
}

/** The audio selection bar's Generate audio: asks only when a selected item already has a take. */
export function generateAudioChange(items: readonly ParagraphItemDto[]): PermanentChange | null {
  const withAudio = items.filter((i) => i.audioFileName).length;
  if (withAudio === 0) return null;
  return {
    action: 'Generate audio',
    scope: `Replaces existing audio on ${plural(withAudio, 'item')}.`,
    cost: 'The current takes cannot be brought back.',
    destructive: true,
  };
}

/**
 * A speaker chip pick (`characterId` set) or clear (null) over the items the host will stamp: asks
 * only when the change discards audio, the delivery-cue precedent. An item already on the target
 * speaker keeps its audio (the host's idempotent stamp), so it does not count.
 */
export function speakerChange(
  items: readonly ParagraphItemDto[],
  characterId: Guid | null,
): PermanentChange | null {
  const discarded = items.filter(
    (i) => !i.isPause && i.audioFileName && i.characterId !== characterId,
  ).length;
  if (discarded === 0) return null;
  const clearing = characterId === null;
  return {
    action: clearing ? 'Clear' : 'Assign',
    scope: `Discards audio on ${plural(discarded, 'item')}.`,
    cost: clearing
      ? 'It must be generated again once the line has a speaker.'
      : 'It must be generated again for the new speaker.',
    destructive: true,
  };
}

/**
 * The items a paragraph speaker write stamps, as the host sweeps them: the dialog items, or every
 * speech item when the paragraph holds no dialog (assigning the narrator to a whole paragraph).
 */
export function paragraphSweep(paragraph: ParagraphDto): ParagraphItemDto[] {
  const speech = paragraph.items.filter((i) => !i.isPause);
  const dialog = speech.filter(isDialog);
  return dialog.length > 0 ? dialog : speech;
}
