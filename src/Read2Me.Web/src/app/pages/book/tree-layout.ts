import { computed, signal } from '@app/core/signals';

const WIDTH_KEY = 'r2m.tree.width';
const OPEN_KEY = 'r2m.tree.open';

/** The structure tree's width, in px (spec #309, "Tree layout state"). */
export const TREE_MIN_PX = 220;
export const TREE_DEFAULT_PX = 280;
const TREE_CAP_PX = 480;
/** The reader's least width beside the tree, and the Book body's column gap between them. */
const READER_MIN_PX = 450;
const BODY_GAP_PX = 16;

/**
 * The structure tree's width and Show/Hide, remembered per browser and shared by every book, like
 * the nav-rail preference. Storage failures (private mode, blocked storage) degrade to 280px and
 * shown, never to an error.
 *
 * The **saved** width is the user's: only {@link setWidth} (a drag or a key press) changes it. The
 * **displayed** {@link width} is it clamped to [220, {@link max}], where the max leaves the reader
 * 450px of the measured Book body; a narrow window shows it clamped without losing it.
 */
export class TreeLayout {
  readonly #open = signal(readOpen());
  readonly open = this.#open.asReadonly();
  readonly #saved = signal(readWidth());
  /** The measured Book body, or null before the first measure (the max is then 480px). */
  readonly #bodyWidth = signal<number | null>(null);

  /** The widest the tree may be now: 480px, less when the reader would drop below 450px. */
  readonly max = computed(() => {
    const body = this.#bodyWidth();
    const room = body === null ? TREE_CAP_PX : body - BODY_GAP_PX - READER_MIN_PX;
    return Math.max(TREE_MIN_PX, Math.min(TREE_CAP_PX, Math.floor(room)));
  });

  /** The displayed width. */
  readonly width = computed(() => Math.min(Math.max(this.#saved(), TREE_MIN_PX), this.max()));

  /** The user's own resize: saved for the next page load and every book. */
  setWidth(width: number): void {
    this.#saved.set(width);
    write(WIDTH_KEY, String(width));
  }

  setOpen(open: boolean): void {
    this.#open.set(open);
    write(OPEN_KEY, open ? '1' : '0');
  }

  /** The Book body was measured; it moves the max and the displayed width, and never saves. */
  setBodyWidth(width: number): void {
    this.#bodyWidth.set(width);
  }
}

function readOpen(): boolean {
  return read(OPEN_KEY) !== '0';
}

function readWidth(): number {
  const saved = Number(read(WIDTH_KEY));
  return Number.isFinite(saved) && saved > 0 ? saved : TREE_DEFAULT_PX;
}

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // storage unavailable: keep the in-memory value
  }
}
