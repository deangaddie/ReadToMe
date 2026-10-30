import type { Block, NodeLevel } from '@app/api';
import type { NodeStatusSummary } from '@app/live/live-messages';
import type { NodeMenuTarget, SelectionKind } from './node-menu-entries';

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
  /** The editor is locked (stale view, a write in flight, or a History step viewed). */
  locked: boolean;
  /** Why it is read-only: a History step is viewed. Null on the current project. */
  readOnly: string | null;
}

export interface StructureNodeMenu {
  target: NodeMenuTarget;
  /** The node's attribution is processing or queued, or the editor is locked: the trigger is off. */
  disabled: boolean;
  /** The trigger's tooltip while a History step is viewed; null otherwise. */
  reason: string | null;
}

/** The trigger's edit gate answer for `menu`: null while it is on. */
export function menuBlock({ disabled, reason }: StructureNodeMenu): Block | null {
  return disabled || reason !== null ? { reason } : null;
}

export function structureNodeMenu({
  node,
  position,
  selection,
  status,
  locked,
  readOnly,
}: StructureNodeMenuInput): StructureNodeMenu {
  const target: NodeMenuTarget = {
    kind: node.level,
    id: node.id,
    text: node.rawTitle,
    isFirst: position.isFirst,
    isLast: position.isLast,
  };
  const busy = !!status && (status.attributionProcessing || status.attributionQueued > 0);
  return {
    target: selection ? { ...target, selection } : target,
    disabled: busy || locked,
    reason: readOnly,
  };
}
