/**
 * Where the Pronunciations panel sits (spec #309, "Pronunciations panel dock check"): docked as the
 * Book body's third column, or floating over the reader's right edge when the columns don't fit.
 */
export type PronunciationsDock = 'docked' | 'floating';

/** The docked panel's column (`.book__body--pron` in book.css). */
const PANEL_PX = 320;
/** The Book body's column gap, `--r2m-space-4`. */
const GAP_PX = 16;
/**
 * The reader's least width between the tree and the docked panel: the settled look's ~450 px,
 * exactly what the 280 px tree's old 1100 px container query left it.
 */
const READER_PX = 468;
/** The tree hidden: the panel docks on a Book body wider than this (unchanged breakpoint). */
const TREE_HIDDEN_PX = 860;

/**
 * Docks when tree + reader + panel and their gaps fit in the Book body, measured; `treeWidth` is
 * the tree column's measured width, or null while the tree is hidden.
 */
export function pronunciationsDock(
  bodyWidth: number,
  treeWidth: number | null,
): PronunciationsDock {
  const needs =
    treeWidth === null ? TREE_HIDDEN_PX : treeWidth + GAP_PX + READER_PX + GAP_PX + PANEL_PX;
  return bodyWidth > needs ? 'docked' : 'floating';
}
