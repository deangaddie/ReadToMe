import { html, nothing } from 'lit-html';
import type { IconName } from './icons';
import { icon } from './partials';
import { rovingKeydown } from './roving';

/**
 * Closes the `[popover].r2m-menu` the clicked entry sits in before its action runs: the browser
 * keeps a popover open across a click inside it. Top-layer calls are optional because happy-dom
 * has no popover API (docs/agents/web/conventions.md, 11).
 */
export function closeMenuOf(e: Event): void {
  const menu = (e.currentTarget as HTMLElement).closest<HTMLElement>('[popover]');
  if (menu?.matches(':popover-open')) menu.hidePopover?.();
}

export interface MenuItem {
  label: string;
  run: () => void;
  icon?: IconName;
  /** A muted second line: the entry's advice, shown before it is clicked. */
  hint?: string;
  /** The `data-action` tests and E2E find the entry by. */
  action?: string;
  destructive?: boolean;
  disabled?: boolean;
}

/** One `r2m-menu` entry: closes its popover, then runs. */
export function menuItem(item: MenuItem) {
  const classes = [
    'r2m-menu__item',
    item.hint ? 'r2m-menu__item--hinted' : '',
    item.destructive ? 'r2m-menu__item--destructive' : '',
  ].join(' ');
  return html`<button
    type="button"
    role="menuitem"
    class=${classes}
    data-action=${item.action ?? nothing}
    ?disabled=${item.disabled ?? false}
    @click=${(e: Event) => {
      closeMenuOf(e);
      item.run();
    }}
  >
    ${item.icon ? icon(item.icon) : nothing}<span class="r2m-menu__text"
      ><span class="r2m-menu__label">${item.label}</span>${
        item.hint ? html`<span class="r2m-menu__hint">${item.hint}</span>` : nothing
      }</span
    >
  </button>`;
}

/** A non-interactive label over a group of entries; not a menuitem, so focus skips it. */
export function menuHeading(text: string) {
  return html`<div class="r2m-menu__heading" role="presentation">${text}</div>`;
}

/**
 * The `popovertarget` id of the trigger the last Enter or Space landed on, or null once a pointer
 * presses: a popover opening with this id was opened from the keyboard.
 */
let keyedTarget: string | null = null;
document.addEventListener(
  'keydown',
  (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    keyedTarget =
      (e.target as Element).closest?.('[popovertarget]')?.getAttribute('popovertarget') ?? null;
  },
  true,
);
document.addEventListener('pointerdown', () => (keyedTarget = null), true);

const ITEM_SELECTOR = '[role="menuitem"], [role="menuitemradio"], [role="menuitemcheckbox"]';

/** The items of the `role="menu"` `panel`, not those of a menu nested in it. */
function itemsOf(panel: HTMLElement): HTMLElement[] {
  return Array.from(panel.querySelectorAll<HTMLElement>(ITEM_SELECTOR)).filter(
    (item) => item.closest('[role="menu"]') === panel,
  );
}

function isEnabled(item: HTMLElement): boolean {
  return !(item as HTMLButtonElement).disabled && item.getAttribute('aria-disabled') !== 'true';
}

/**
 * The menu keyboard for a `[popover][role="menu"]` (WAI-ARIA menu pattern): bind `keydown` and
 * `toggle` on the popover. Up and Down move between enabled items (wrapping), Home and End go to
 * the first and last, and only one item is in the tab order, so Tab leaves the menu. An open
 * after Enter or Space on the trigger focuses the first enabled item; a pointer open leaves focus
 * where it is. Escape and light dismiss stay the browser's, which returns focus to the trigger.
 * The node menu has its own keys (submenus, type-ahead) over the same `rovingKeydown`.
 */
export const menuKeys = {
  keydown: (e: KeyboardEvent): void => {
    rovingKeydown(e, itemsOf(e.currentTarget as HTMLElement).filter(isEnabled), 'vertical');
  },
  toggle: (e: Event): void => {
    if ((e as ToggleEvent).newState !== 'open') return;
    const panel = e.currentTarget as HTMLElement;
    const items = itemsOf(panel);
    const first = items.find(isEnabled);
    for (const item of items) item.tabIndex = item === first ? 0 : -1;
    if (panel.id && keyedTarget === panel.id) first?.focus();
    keyedTarget = null;
  },
};
