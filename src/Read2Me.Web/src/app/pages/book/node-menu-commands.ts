import { BookCommand, InsertPosition, MergeDirection } from '@app/api';
import { ConfirmOptions } from '@app/ui/confirm-dialog/confirm-dialog';
import { TextPromptOptions } from '@app/ui/text-prompt-dialog/text-prompt-dialog';
import {
  HAS_CHILDREN,
  MenuEntryId,
  NodeMenuKind,
  NodeMenuTarget,
  mergeDirectionOf,
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

/**
 * Turns a chosen menu entry into the command to post, asking whatever the entry needs first
 * (ticket 11, research §3). Null means nothing to send: the dialog was cancelled, or the answer
 * would change nothing (an unchanged title or text is not sent, as in Blazor).
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
      return null;
    }
  }
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
