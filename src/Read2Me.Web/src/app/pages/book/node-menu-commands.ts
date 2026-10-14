import type { BookCommand, InsertPosition, MergeDirection, NodeKind } from '@app/api';
import type { ConfirmOptions, TextPromptOptions } from '@app/ui/dialogs';
import {
  HAS_CHILDREN,
  type MenuEntryId,
  type NodeMenuKind,
  type NodeMenuTarget,
  PLACEMENT_WORDS,
  insertedKind,
  mergeDirectionOf,
  nodeInsertOf,
  pauseInsertOf,
} from './node-menu-entries';

/** The two dialogs a menu flow may open — `PromptService.text` and `ConfirmService.confirm`. */
export interface MenuDialogs {
  text(options: TextPromptOptions): Promise<string | null>;
  confirm(options: ConfirmOptions): Promise<boolean>;
}

const WORD: Record<NodeMenuKind, string> = {
  volume: 'volume',
  part: 'part',
  chapter: 'chapter',
  paragraph: 'paragraph',
  item: 'item',
  'pause-paragraph': 'pause',
};

const NAME_LIMIT = 60;

/** Which TTS models act on a delivery cue (spec story 16); static, not tied to the active provider. */
export const CUE_HINT = 'Followed by Breeze; VoxCPM2 only loosely; ignored by Chatterbox and Qwen3.';

/**
 * Turns a chosen menu entry into the command to post, asking whatever the entry needs first
 * (ticket 11, research §3). Null means nothing to send: the dialog was cancelled, or the answer
 * would change nothing (an unchanged title or text is not sent).
 */
export async function commandFor(
  entry: MenuEntryId,
  target: NodeMenuTarget,
  dialogs: MenuDialogs,
): Promise<BookCommand | null> {
  const { kind, id } = target;

  switch (entry) {
    case 'edit-title': {
      const title = await dialogs.text({
        title: `Edit ${WORD[kind]} title`,
        label: 'Title',
        initial: target.text ?? '',
        required: true,
      });
      if (title === null || title === (target.text ?? '').trim()) return null;
      return titleCommand(kind, id, title);
    }

    case 'edit-text': {
      const text = await dialogs.text({
        title: 'Edit item text',
        label: 'Text',
        initial: target.text ?? '',
        multiline: true,
        required: true,
      });
      if (text === null || text === (target.text ?? '').trim()) return null;
      return { type: 'UpdateParagraphItemText', itemId: id, text };
    }

    case 'add-cue':
    case 'edit-cue': {
      const current = target.cue?.trim() ?? '';
      const cue = await dialogs.text({
        title: entry === 'add-cue' ? 'Add delivery cue' : 'Edit delivery cue',
        label: 'Delivery cue',
        initial: current,
        hint: CUE_HINT,
      });
      // Saving empty removes the cue; an unchanged cue is not sent (it would keep the audio anyway).
      if (cue === null || cue === current) return null;
      return cueCommand(target, cue || null, dialogs);
    }

    case 'remove-cue':
      return cueCommand(target, null, dialogs);

    case 'split':
      return splitCommand(target, dialogs);

    case 'insert-before':
    case 'insert-after': {
      const position: InsertPosition = entry === 'insert-before' ? 'Before' : 'After';
      const text = await dialogs.text({
        title: `Insert item ${position.toLowerCase()}`,
        label: 'Text',
        multiline: true,
        required: true,
      });
      if (text === null) return null;
      return { type: 'InsertParagraphItem', anchorItemId: id, position, text };
    }

    case 'delete': {
      const name = displayName(target);
      const contents = HAS_CHILDREN[kind] ? ' This will also delete all contents it contains.' : '';
      const ok = await dialogs.confirm({
        title: `Delete ${WORD[kind]}`,
        message: `Delete “${name}”?${contents} This cannot be undone.`,
        destructive: true,
        confirmLabel: 'Delete',
      });
      return ok ? deleteCommand(kind, id) : null;
    }

    default: {
      const direction = mergeDirectionOf(entry);
      if (direction) return mergeCommand(kind, id, direction);
      const pause = pauseInsertOf(entry);
      if (pause) {
        return {
          type: 'InsertPauseParagraph',
          anchorItemId: id,
          position: pause.position,
          pauseKind: pause.kind,
        };
      }
      const placement = nodeInsertOf(entry);
      if (placement) {
        const created = insertedKind(kind, placement);
        const anchor = { type: 'InsertNode', anchorKind: ANCHOR_KIND[kind], anchorId: id, placement } as const;
        if (created !== 'paragraph' && created !== 'item') {
          // A new volume, part or chapter is born empty; its title is optional, renamable later.
          const title = await dialogs.text({
            title: `Insert ${created} ${PLACEMENT_WORDS[placement]}`,
            label: 'Title',
            placeholder: 'Leave blank for no title',
          });
          if (title === null) return null;
          return { ...anchor, title: title || null };
        }
        // A new paragraph is never empty — an empty one reads as a pause — and nor is a speech
        // item at a paragraph's start or end, so its text is required.
        const text = await dialogs.text({
          title: `Insert ${created} ${PLACEMENT_WORDS[placement]}`,
          label: 'Text',
          multiline: true,
          required: true,
        });
        if (text === null) return null;
        return { ...anchor, text };
      }
      return null;
    }
  }
}

/** The anchor kind InsertNode is told; a pause paragraph is a Paragraph to the host. */
const ANCHOR_KIND: Record<NodeMenuKind, NodeKind> = {
  volume: 'Volume',
  part: 'Part',
  chapter: 'Chapter',
  paragraph: 'Paragraph',
  item: 'ParagraphItem',
  'pause-paragraph': 'Paragraph',
};

/**
 * Sets or removes an item's cue. A real change discards the item's audio, so an item that has
 * audio asks first (delivery-cue-editing 04); text edits deliberately do not (spec, Further Notes).
 */
async function cueCommand(
  target: NodeMenuTarget,
  cue: string | null,
  dialogs: MenuDialogs,
): Promise<BookCommand | null> {
  if (target.hasAudio) {
    const [verb, lead] =
      cue === null
        ? ['Remove', 'Removing the delivery cue']
        : target.cue
          ? ['Change', 'Changing the delivery cue']
          : ['Add', 'Adding a delivery cue'];
    const ok = await dialogs.confirm({
      title: `${verb} delivery cue`,
      message: `${lead} discards this item's audio.`,
      confirmLabel: verb,
      destructive: true,
    });
    if (!ok) return null;
  }
  return { type: 'SetItemDeliveryCue', itemId: target.id, cue };
}

async function splitCommand(
  target: NodeMenuTarget,
  dialogs: MenuDialogs,
): Promise<BookCommand | null> {
  const { kind, id } = target;
  if (kind === 'item') return { type: 'SplitAtItem', itemId: id };

  const created = kind === 'part' ? 'volume' : kind === 'chapter' ? 'part' : 'chapter';
  const title = await dialogs.text({
    title: `New ${created} title`,
    label: 'Title',
    placeholder: 'Leave blank for no title',
  });
  if (title === null) return null;
  const newTitle = title || null;
  switch (kind) {
    case 'part':
      return { type: 'SplitAtPart', partId: id, newVolumeTitle: newTitle };
    case 'chapter':
      return { type: 'SplitAtChapter', chapterId: id, newPartTitle: newTitle };
    case 'paragraph':
      return { type: 'SplitAtParagraph', paragraphId: id, newChapterTitle: newTitle };
    default:
      return null;
  }
}

function titleCommand(kind: NodeMenuKind, id: string, title: string): BookCommand | null {
  switch (kind) {
    case 'volume':
      return { type: 'UpdateVolumeTitle', volumeId: id, title };
    case 'part':
      return { type: 'UpdatePartTitle', partId: id, title };
    case 'chapter':
      return { type: 'UpdateChapterTitle', chapterId: id, title };
    default:
      return null;
  }
}

function mergeCommand(kind: NodeMenuKind, id: string, direction: MergeDirection): BookCommand | null {
  switch (kind) {
    case 'volume':
      return { type: 'MergeVolume', volumeId: id, direction };
    case 'part':
      return { type: 'MergePart', partId: id, direction };
    case 'chapter':
      return { type: 'MergeChapter', chapterId: id, direction };
    case 'paragraph':
      return { type: 'MergeParagraph', paragraphId: id, direction };
    case 'item':
      return { type: 'MergeParagraphItem', itemId: id, direction };
    default:
      return null;
  }
}

function deleteCommand(kind: NodeMenuKind, id: string): BookCommand {
  switch (kind) {
    case 'volume':
      return { type: 'DeleteVolume', volumeId: id };
    case 'part':
      return { type: 'DeletePart', partId: id };
    case 'chapter':
      return { type: 'DeleteChapter', chapterId: id };
    case 'item':
      return { type: 'DeleteParagraphItem', itemId: id };
    default:
      return { type: 'DeleteParagraph', paragraphId: id };
  }
}

/** What the delete confirm calls the node: its title or text (shortened), else "this paragraph". */
export function displayName(target: NodeMenuTarget): string {
  const text = target.text?.trim() ?? '';
  if (!text) return `this ${WORD[target.kind]}`;
  return text.length > NAME_LIMIT ? `${text.slice(0, NAME_LIMIT)}…` : text;
}
