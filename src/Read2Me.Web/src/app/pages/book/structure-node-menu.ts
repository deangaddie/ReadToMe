import { type Block, type NodeLevel, withLocal } from '@app/api';
import type { NodeStatusSummary } from '@app/live/live-messages';
import { MENU_KIND_OF_LEVEL, type NodeMenuTarget, type SelectionKind } from './node-menu-entries';

/**
 * What a Volume, Part or Chapter's Node menu offers, decided once for every pane that shows the
 * node (the structure tree, the Book page headings), so each offers exactly the same entries.
 */
export interface StructureNodeMenuInput {
  node: {
    level: NodeLevel;
    id: string;
    /** The stored title, null when untitled: the menu edits it, never the placeholder. */
    rawTitle: string | null;
  };
  /** Position among siblings of the same level: the merge entries hide at the ends. */
  position: { isFirst: boolean; isLast: boolean };
  /** Which selection the reader is in (the current Reader mode's shortcuts); null for none. */
  selection: SelectionKind | null;
  /** The node's roll-up; null when it has none. */
  status: NodeStatusSummary | null;
  /** The edit gate's answer for the page (`BookEditor.blocked()`); null while edits are open. */
  blocked: Block | null;
}

export interface StructureNodeMenu {
  target: NodeMenuTarget;
  /** The trigger's edit gate answer: `blocked` with the node's attribution busy folded in. */
  block: Block | null;
}

export function structureNodeMenu({
  node,
  position,
  selection,
  status,
  blocked,
}: StructureNodeMenuInput): StructureNodeMenu {
  const target: NodeMenuTarget = {
    kind: MENU_KIND_OF_LEVEL[node.level],
    id: node.id,
    text: node.rawTitle,
    isFirst: position.isFirst,
    isLast: position.isLast,
  };
  const busy = !!status && (status.attributionProcessing || status.attributionQueued > 0);
  return {
    target: selection ? { ...target, selection } : target,
    block: withLocal(blocked, busy),
  };
}
