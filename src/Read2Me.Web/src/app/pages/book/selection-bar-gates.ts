import { type Block, withLocal } from '@app/api';

/**
 * The Edit gates of the two selection action bars on the Book page, as pure functions of plain
 * inputs through the one fold (`withLocal`, context/web.md "Edit gate"). The page passes each
 * result to `gated()`. Clear has no gate: it changes only what the page has selected, so it stays
 * on, even while a History step is viewed.
 */

/** Bulk assign's local **Blocked reason** while the attribution queue is busy. */
export const BULK_WAITS_REASON = 'Bulk assign waits until attribution has finished';

/** What the paragraph bar (Read and Speakers modes) reads. */
export interface ParagraphBarInputs {
  /** The page's Edit gate, `editor.block()`. */
  readonly block: Block | null;
  /** A selection intent is in flight (`SelectionWorkflow.working`). */
  readonly working: boolean;
  /** Attribution runs somewhere (research §3): Bulk assign waits for it. */
  readonly queueBusy: boolean;
}

export interface ParagraphBarGates {
  readonly attribute: Block | null;
  readonly bulkAssign: Block | null;
}

/**
 * Attribute is the page's gate with the workflow's busy folded in. Bulk assign is Attribute's
 * gate plus its own wait while attribution runs; viewing and stale still win over that wait.
 */
export function paragraphBarGates({
  block,
  working,
  queueBusy,
}: ParagraphBarInputs): ParagraphBarGates {
  const attribute = withLocal(block, working);
  return { attribute, bulkAssign: withLocal(attribute, queueBusy && BULK_WAITS_REASON) };
}

/** What the item bar (Audio mode) reads. */
export interface ItemBarInputs {
  /** The page's Edit gate, `editor.block()`. */
  readonly block: Block | null;
  /** A selection intent is in flight (`SelectionWorkflow.working`). */
  readonly working: boolean;
  /** An audio preflight or enqueue is in flight (`AudioGenerator.working`). */
  readonly generating: boolean;
}

export interface ItemBarGates {
  readonly generate: Block | null;
}

/** Generate audio is the page's gate with either busy folded in, without a reason. */
export function itemBarGates({ block, working, generating }: ItemBarInputs): ItemBarGates {
  return { generate: withLocal(block, working || generating) };
}
