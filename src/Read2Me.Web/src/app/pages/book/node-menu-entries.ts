import { InsertPosition, MergeDirection, PauseKind } from '@app/api';

/**
 * The node menu's entry set (ticket 11, research §3 "Node menu"), as a pure table over the node's
 * kind and position. The component renders whatever this returns; the flows in
 * `node-menu-commands.ts` turn a chosen entry into a command. Tested as a matrix once here.
 */

export type NodeMenuKind = 'volume' | 'part' | 'chapter' | 'paragraph' | 'item' | 'pause-paragraph';

export interface NodeMenuTarget {
  kind: NodeMenuKind;
  id: string;
  /** The raw title (nodes), text (items) or pause label (pause paragraphs); null when untitled. */
  text: string | null;
  /** Position among siblings: merge entries hide at the ends. */
  isFirst: boolean;
  isLast: boolean;
  /** Items only: a pause item anchors no item insert. */
  isPause?: boolean;
}

export type MenuEntryId =
  | 'edit-title'
  | 'edit-text'
  | 'split'
  | 'merge-previous'
  | 'merge-next'
  | 'insert-before'
  | 'insert-after'
  | `pause-${Lowercase<InsertPosition>}:${PauseKind}`
  | 'delete';

export type MenuGroup = 'edit' | 'structure' | 'insert' | 'pause-before' | 'pause-after' | 'delete';

export interface MenuEntry {
  id: MenuEntryId;
  label: string;
  icon: string;
  group: MenuGroup;
  destructive?: boolean;
}

export const PAUSE_KINDS: readonly { kind: PauseKind; label: string }[] = [
  { kind: 'Pause', label: 'Pause' },
  { kind: 'ParagraphPause', label: 'Paragraph pause' },
  { kind: 'ChapterPause', label: 'Chapter pause' },
  { kind: 'PartPause', label: 'Part pause' },
  { kind: 'VolumePause', label: 'Volume pause' },
];

/** What a split at this kind creates, and so what its entry is called. */
const SPLIT_LABEL: Partial<Record<NodeMenuKind, string>> = {
  part: 'Split volume',
  chapter: 'Split part',
  paragraph: 'Split chapter',
  item: 'Split paragraph',
};

export const DELETE_LABEL: Record<NodeMenuKind, string> = {
  volume: 'Delete volume',
  part: 'Delete part',
  chapter: 'Delete chapter',
  paragraph: 'Delete paragraph',
  item: 'Delete item',
  'pause-paragraph': 'Delete pause',
};

/** Deleting a node that holds others says so in its confirm; leaves do not. */
export const HAS_CHILDREN: Record<NodeMenuKind, boolean> = {
  volume: true,
  part: true,
  chapter: true,
  paragraph: false,
  item: false,
  'pause-paragraph': false,
};

export function menuEntries(target: NodeMenuTarget): MenuEntry[] {
  const { kind } = target;
  const entries: MenuEntry[] = [];

  if (kind === 'volume' || kind === 'part' || kind === 'chapter') {
    entries.push({ id: 'edit-title', label: 'Edit title', icon: 'edit', group: 'edit' });
  } else if (kind === 'item') {
    entries.push({ id: 'edit-text', label: 'Edit text', icon: 'edit', group: 'edit' });
  }

  const split = SPLIT_LABEL[kind];
  if (split) entries.push({ id: 'split', label: split, icon: 'call_split', group: 'structure' });

  if (kind !== 'pause-paragraph') {
    if (!target.isFirst) {
      entries.push({
        id: 'merge-previous',
        label: 'Merge with previous',
        icon: 'merge',
        group: 'structure',
      });
    }
    if (!target.isLast) {
      entries.push({ id: 'merge-next', label: 'Merge with next', icon: 'merge', group: 'structure' });
    }
  }

  if (kind === 'item') {
    if (!target.isPause) {
      entries.push(
        { id: 'insert-before', label: 'Insert item before', icon: 'add', group: 'insert' },
        { id: 'insert-after', label: 'Insert item after', icon: 'add', group: 'insert' },
      );
    }
    for (const position of ['Before', 'After'] as const) {
      const group: MenuGroup = position === 'Before' ? 'pause-before' : 'pause-after';
      for (const { kind: pauseKind, label } of PAUSE_KINDS) {
        entries.push({
          id: `pause-${position.toLowerCase() as Lowercase<InsertPosition>}:${pauseKind}`,
          label,
          icon: 'pause',
          group,
        });
      }
    }
  }

  entries.push({
    id: 'delete',
    label: DELETE_LABEL[kind],
    icon: 'delete',
    group: 'delete',
    destructive: true,
  });
  return entries;
}

/** The merge direction an entry names, or null for any other entry. */
export function mergeDirectionOf(id: MenuEntryId): MergeDirection | null {
  return id === 'merge-previous' ? 'Previous' : id === 'merge-next' ? 'Next' : null;
}

/** The pause insertion an entry names, or null for any other entry. */
export function pauseInsertOf(id: MenuEntryId): { position: InsertPosition; kind: PauseKind } | null {
  const match = /^pause-(before|after):(\w+)$/.exec(id);
  if (!match) return null;
  return {
    position: match[1] === 'before' ? 'Before' : 'After',
    kind: match[2] as PauseKind,
  };
}
