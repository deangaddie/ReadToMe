/**
 * Closes the `[popover].r2m-menu` the clicked entry sits in before its action runs: the browser
 * keeps a popover open across a click inside it. Top-layer calls are optional because happy-dom
 * has no popover API (web.md, conventions 11).
 */
export function closeMenuOf(e: Event): void {
  const menu = (e.currentTarget as HTMLElement).closest<HTMLElement>('[popover]');
  if (menu?.matches(':popover-open')) menu.hidePopover?.();
}
