/**
 * The roving-tabindex and type-ahead helper shared by `tabs()`, the node menu and the structure tree
 * (spec §2.3 point 8): one tab stop per composite widget, arrows and Home/End move it. The CDK `ListKeyManager` keys,
 * in a function so each widget keeps its own markup.
 */
export type RovingOrientation = 'horizontal' | 'vertical' | 'both';

const NEXT: Record<RovingOrientation, readonly string[]> = {
  horizontal: ['ArrowRight'],
  vertical: ['ArrowDown'],
  both: ['ArrowRight', 'ArrowDown'],
};
const PREVIOUS: Record<RovingOrientation, readonly string[]> = {
  horizontal: ['ArrowLeft'],
  vertical: ['ArrowUp'],
  both: ['ArrowLeft', 'ArrowUp'],
};

/** The index `key` moves to from `current` among `count` items (wrapping), or null for other keys. */
export function rovingTarget(
  key: string,
  current: number,
  count: number,
  orientation: RovingOrientation,
): number | null {
  if (count === 0) return null;
  if (key === 'Home') return 0;
  if (key === 'End') return count - 1;
  if (NEXT[orientation].includes(key)) return (current + 1) % count;
  if (PREVIOUS[orientation].includes(key)) return (current - 1 + count) % count;
  return null;
}

/**
 * Handles a keydown on a composite widget: moves focus and the `tabindex="0"` stop to the item
 * the key names, from the item that has focus (the first when none does). Returns the item moved
 * to, or null when the key was not a roving key.
 */
export function rovingKeydown(
  event: KeyboardEvent,
  items: readonly HTMLElement[],
  orientation: RovingOrientation,
): HTMLElement | null {
  const current = Math.max(
    0,
    items.findIndex(
      (item) => item === document.activeElement || item.contains(document.activeElement),
    ),
  );
  const index = rovingTarget(event.key, current, items.length, orientation);
  if (index === null) return null;
  event.preventDefault();
  const target = items[index]!;
  for (const item of items) item.tabIndex = item === target ? 0 : -1;
  target.focus();
  return target;
}

/** How long typing may pause before the collected letters are looked up (CDK's default). */
export const TYPE_AHEAD_MS = 200;

/**
 * The index of the first label that starts with `query` (case-insensitive, whitespace trimmed),
 * searched from the item after `current` and wrapping round so `current` itself is tried last —
 * CDK's `ListKeyManager` type-ahead rule. Null for no match, an empty query or an empty list.
 */
export function typeAheadTarget(
  query: string,
  labels: readonly string[],
  current: number,
): number | null {
  const wanted = query.trim().toLocaleUpperCase();
  if (!wanted || labels.length === 0) return null;
  for (let step = 1; step <= labels.length; step++) {
    const index = (current + step) % labels.length;
    if (labels[index]!.trim().toLocaleUpperCase().startsWith(wanted)) return index;
  }
  return null;
}

/**
 * Collects type-ahead letters from keydowns and hands the query over once typing pauses for
 * {@link TYPE_AHEAD_MS}. A printable key without Ctrl/Alt/Meta is consumed (default prevented);
 * space, named keys and shortcuts are left for the widget. One instance per widget.
 */
export class TypeAhead {
  #query = '';
  #timer: ReturnType<typeof setTimeout> | undefined;

  constructor(
    private readonly onQuery: (query: string) => void,
    private readonly debounceMs = TYPE_AHEAD_MS,
  ) {}

  /** True when the key was taken into the query. */
  keydown(event: KeyboardEvent): boolean {
    if (event.key.length !== 1 || event.key === ' ') return false;
    if (event.ctrlKey || event.altKey || event.metaKey) return false;
    event.preventDefault();
    this.#query += event.key;
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => {
      const query = this.#query;
      this.#query = '';
      this.#timer = undefined;
      this.onQuery(query);
    }, this.debounceMs);
    return true;
  }

  /** Drops whatever was typed so far (the widget closed or lost focus). */
  cancel(): void {
    clearTimeout(this.#timer);
    this.#timer = undefined;
    this.#query = '';
  }
}
