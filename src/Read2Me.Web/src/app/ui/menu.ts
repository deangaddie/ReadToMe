import { html, nothing } from 'lit-html';
import type { IconName } from './icons';
import { icon } from './partials';

/**
 * Closes the `[popover].r2m-menu` the clicked entry sits in before its action runs: the browser
 * keeps a popover open across a click inside it. Top-layer calls are optional because happy-dom
 * has no popover API (web.md, conventions 11).
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
