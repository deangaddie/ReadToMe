import { type Block, ITEM_AUDIO_NOT_KEPT } from '@app/api';
import type { SelectionKind } from './node-menu-entries';
import type { RowContext } from './reader-rows';

/** No Pronunciation marks lit. */
export const NO_LIT: ReadonlySet<string> = new Set();

/**
 * What the Book page reads to build its {@link RowContext}, as plain values: the values a row
 * reads as they are, and the few it derives from (the selection kind, the Edit gate, viewing, the
 * Pronunciations panel).
 */
export interface RowContextInputs extends Omit<
  RowContext,
  'folder' | 'selectable' | 'itemSelectable' | 'blocked' | 'playBlock' | 'litMarks'
> {
  /** The open project's folder, or null before it is known. */
  readonly folder: string | null;
  /** Which selection the workflow has active: it decides which rows offer a checkbox. */
  readonly kind: SelectionKind;
  /** The page's Edit gate, `editor.block()`. */
  readonly block: Block | null;
  /** A History step is viewed. */
  readonly viewing: boolean;
  /** The Pronunciations panel is open. */
  readonly pronunciationsOpen: boolean;
  /** The entries the panel lights (the row hovered, the entry it edits). */
  readonly lit: ReadonlySet<string>;
}

/**
 * The reader rows' context. Paragraph rows are selectable over a paragraph selection and item
 * rows over an item one; item play is off with `ITEM_AUDIO_NOT_KEPT` only while a step is viewed
 * (it stays on while stale); marks light only while the panel is open.
 */
export function rowContext({
  folder,
  kind,
  block,
  viewing,
  pronunciationsOpen,
  lit,
  ...values
}: RowContextInputs): RowContext {
  return {
    ...values,
    folder: folder ?? '',
    selectable: kind === 'paragraphs',
    itemSelectable: kind === 'items',
    blocked: block,
    playBlock: viewing ? { reason: ITEM_AUDIO_NOT_KEPT } : null,
    litMarks: pronunciationsOpen ? lit : NO_LIT,
  };
}
