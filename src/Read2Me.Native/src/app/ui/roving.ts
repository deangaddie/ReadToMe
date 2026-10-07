/**
 * The roving-tabindex helper shared by `tabs()`, menus and the structure tree (spec §2.3 point 8):
 * one tab stop per composite widget, arrows and Home/End move it. The CDK `ListKeyManager` keys,
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
