import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { icon } from '@app/ui/partials';
import { TypeAhead, rovingKeydown, typeAheadTarget } from '@app/ui/roving';
import { BookEditor } from './book-editor';
import { commandFor } from './node-menu-commands';
import {
  type ActionEntryId,
  type MenuEntry,
  type MenuEntryId,
  type NodeMenuTarget,
  isActionEntry,
  menuEntries,
} from './node-menu-entries';
import nodeMenuCss from './node-menu.css' with { type: 'text' };

adoptStyles(nodeMenuCss);

/** The event a trigger dispatches (bubbling) to reach the shared menu above it. */
export const NODE_MENU_OPEN = 'r2m-node-menu-open';

/** What one open of the menu needs; the opener's ancestors may fill in the optional parts. */
export interface NodeMenuRequest {
  /** The element the panel is anchored to (the trigger button). */
  anchor: HTMLElement;
  target: NodeMenuTarget;
  /** Where focus goes when the menu closes; the anchor when unset. */
  returnTo?: HTMLElement;
  /** Takes the selection entries (tree nodes only); without it they are ignored. */
  onAction?: (action: ActionEntryId) => void;
}

const ANCHOR = '--r2m-node-menu';
const SUB_ANCHOR = '--r2m-node-menu-sub';
const PANEL_ID = 'r2m-node-menu-panel';
type PauseSide = 'before' | 'after';

/**
 * The per-row and per-node menu trigger (the `mat-icon-button` of the Angular `NodeMenu`): a
 * click asks the nearest shared {@link NodeMenu} to open for `target`, anchored here. Disabled by
 * the caller for a busy row; the editor's lock is checked by the menu itself.
 */
export function nodeMenuTrigger(
  target: NodeMenuTarget,
  options: { disabled?: boolean; tabIndex?: number } = {},
) {
  const open = (e: Event) => {
    e.stopPropagation();
    const anchor = e.currentTarget as HTMLElement;
    const detail: NodeMenuRequest = { anchor, target };
    anchor.dispatchEvent(new CustomEvent(NODE_MENU_OPEN, { detail, bubbles: true }));
  };
  return html`<button
    type="button"
    class="r2m-icon-button r2m-node-menu__trigger"
    aria-label=${menuLabel(target)}
    aria-haspopup="menu"
    tabindex=${options.tabIndex ?? nothing}
    ?disabled=${options.disabled ?? false}
    @click=${open}
  >
    ${icon('more_vert')}
  </button>`;
}

export function menuLabel(target: NodeMenuTarget): string {
  return `Actions for ${target.text?.trim() || target.kind}`;
}

/**
 * The node menu (ticket 11, design §6.3): one element for every level of the book, the entry set
 * decided by {@link menuEntries} from the target's kind and position. A chosen entry runs its
 * dialog flow and posts the command through {@link BookEditor}; nothing here patches the rows.
 * The selection entries (ticket 12) are not commands: they go back to the opener as an action.
 *
 * One instance serves every trigger under its parent element (spec §7, risk 2): it listens for
 * {@link NODE_MENU_OPEN} there and re-anchors its popover to the trigger of each open, so a tree
 * re-render under an open menu keeps the anchor (lit keeps the trigger element by id). The panel
 * lives outside the tree and the rows, so its keys never reach theirs; Escape and a chosen entry
 * hand focus back to `returnTo`. Refuses to open while the editor is locked (stale or mid-write).
 */
export class NodeMenu extends R2mElement {
  private editor!: BookEditor;
  private readonly prompt = use(PromptService);
  private readonly confirm = use(ConfirmService);

  readonly #request = signal<NodeMenuRequest | null>(null);
  readonly #sub = signal<PauseSide | null>(null);
  readonly #typeAhead = new TypeAhead((query) => this.#jumpTo(query));
  /** Watches the scope while open: a trigger that leaves the DOM (its node was deleted) closes the menu. */
  readonly #anchorWatch = new MutationObserver(() => {
    const request = this.#request();
    if (request && !request.anchor.isConnected) this.#closeAll();
  });

  readonly #entries = computed(() => {
    const request = this.#request();
    return request ? menuEntries(request.target) : [];
  });
  /** Everything but the pause submenus and the delete, which render in their own places. */
  readonly #main = computed(() =>
    this.#entries().filter((e) => !e.group.startsWith('pause-') && e.group !== 'delete'),
  );
  readonly #pausesBefore = computed(() => this.#entries().filter((e) => e.group === 'pause-before'));
  readonly #pausesAfter = computed(() => this.#entries().filter((e) => e.group === 'pause-after'));
  readonly #deleteEntry = computed(() => this.#entries().find((e) => e.group === 'delete') ?? null);

  /** The menu is open (the entries are rendered and, in a real browser, in the top layer). */
  get isOpen(): boolean {
    return this.#request() !== null;
  }

  protected override connected(): void {
    this.editor = use(BookEditor, this);
    const scope = this.parentElement;
    if (scope) {
      const onOpen = (e: Event) => this.openFor((e as CustomEvent<NodeMenuRequest>).detail);
      scope.addEventListener(NODE_MENU_OPEN, onOpen);
      this.onDisconnect(() => scope.removeEventListener(NODE_MENU_OPEN, onOpen));
    }
    this.onDisconnect(() => this.#release());
  }

  /** Opens the menu for `request`, anchored to its trigger; a second open re-anchors. */
  openFor(request: NodeMenuRequest): void {
    if (this.editor.locked()) return;
    this.#release();
    request.anchor.style.setProperty('anchor-name', ANCHOR);
    request.anchor.setAttribute('aria-expanded', 'true');
    request.anchor.setAttribute('aria-controls', PANEL_ID);
    this.#request.set(request);
    this.#sub.set(null);
    if (this.parentElement) {
      this.#anchorWatch.observe(this.parentElement, { childList: true, subtree: true });
    }
    void this.rendered().then(() => {
      if (this.#request() !== request) return;
      const panel = this.#panel();
      this.#show(panel);
      this.#focusFirst(this.#itemsOf(panel));
    });
  }

  /** Closes the menu without choosing; focus goes back to `returnTo`. */
  close(): void {
    if (!this.isOpen) return;
    const request = this.#request();
    this.#closeAll();
    if (request) this.#restoreFocus(request);
  }

  protected template() {
    const request = this.#request();
    const main = this.#main();
    const pausesBefore = this.#pausesBefore();
    const pausesAfter = this.#pausesAfter();
    const deleteEntry = this.#deleteEntry();
    return html`<div
      id=${PANEL_ID}
      class="r2m-menu r2m-node-menu__panel"
      popover="auto"
      role="menu"
      aria-label=${request ? menuLabel(request.target) : nothing}
      @toggle=${this.#onToggle}
      @keydown=${this.#onKeydown}
    >
      ${
        request
          ? html`${main.map((entry, i) => this.#entry(entry, i > 0 && main[i - 1]!.group !== entry.group))}
            ${
              pausesBefore.length
                ? html`<hr class="r2m-menu__divider" />
                  ${this.#subMenu('before', 'Insert pause before', pausesBefore)}
                  ${this.#subMenu('after', 'Insert pause after', pausesAfter)}`
                : nothing
            }
            ${deleteEntry ? this.#entry(deleteEntry, true) : nothing}`
          : nothing
      }
    </div>`;
  }

  #entry(entry: MenuEntry, divided: boolean) {
    return html`${divided ? html`<hr class="r2m-menu__divider" />` : nothing}<button
        type="button"
        role="menuitem"
        tabindex="-1"
        class="r2m-menu__item ${entry.destructive ? 'r2m-menu__item--destructive' : ''}"
        data-entry=${entry.id}
        @click=${() => void this.#choose(entry.id)}
      >
        ${icon(entry.icon)}<span>${entry.label}</span>
      </button>`;
  }

  /** A submenu entry and, while open, the nested popover with the pause kinds. */
  #subMenu(side: PauseSide, label: string, entries: readonly MenuEntry[]) {
    const open = this.#sub() === side;
    return html`<button
        type="button"
        role="menuitem"
        tabindex="-1"
        class="r2m-menu__item"
        data-entry="pause-${side}"
        aria-haspopup="menu"
        aria-expanded=${open ? 'true' : 'false'}
        @click=${(e: Event) => this.#openSub(side, e.currentTarget as HTMLElement)}
        @mouseenter=${(e: Event) => this.#openSub(side, e.currentTarget as HTMLElement)}
      >
        ${icon('pause')}<span>${label}</span>${icon('chevron_right', 'r2m-node-menu__chevron')}
      </button>
      <div
        class="r2m-menu r2m-node-menu__sub"
        popover="auto"
        role="menu"
        aria-label=${label}
        data-sub=${side}
        @toggle=${(e: ToggleEvent) => {
          if (e.newState === 'closed' && this.#sub() === side) this.#sub.set(null);
        }}
      >
        ${
          open
            ? entries.map(
                (entry) =>
                  html`<button
                    type="button"
                    role="menuitem"
                    tabindex="-1"
                    class="r2m-menu__item"
                    data-entry=${entry.id}
                    @click=${() => void this.#choose(entry.id)}
                  >
                    <span>${entry.label}</span>
                  </button>`,
              )
            : nothing
        }
      </div>`;
  }

  async #choose(id: MenuEntryId): Promise<void> {
    const request = this.#request();
    if (!request) return;
    this.#closeAll();
    this.#restoreFocus(request);
    if (isActionEntry(id)) {
      request.onAction?.(id);
      return;
    }
    const command = await commandFor(id, request.target, {
      text: (o) => this.prompt.text(o),
      confirm: (o) => this.confirm.confirm(o),
    });
    if (command) await this.editor.run(command);
  }

  #openSub(side: PauseSide, trigger: HTMLElement): void {
    if (this.#sub() === side) return;
    this.#hide(this.#subPanel());
    this.querySelector<HTMLElement>(`[style*="${SUB_ANCHOR}"]`)?.style.removeProperty('anchor-name');
    trigger.style.setProperty('anchor-name', SUB_ANCHOR);
    this.#sub.set(side);
    void this.rendered().then(() => {
      if (this.#sub() !== side) return;
      const panel = this.#subPanel();
      this.#show(panel);
      this.#focusFirst(this.#itemsOf(panel));
    });
  }

  /** Closes an open submenu and puts focus back on its entry. */
  #closeSub(): void {
    const side = this.#sub();
    if (!side) return;
    this.#hide(this.#subPanel());
    this.#sub.set(null);
    this.querySelector<HTMLElement>(`[data-entry="pause-${side}"]`)?.focus();
  }

  #closeAll(): void {
    this.#hide(this.#panel());
    this.#release();
  }

  /** Forgets the request and what it put on the trigger (the anchor name, the ARIA state). */
  #release(): void {
    const anchor = this.#request()?.anchor;
    if (anchor) {
      anchor.style.removeProperty('anchor-name');
      anchor.setAttribute('aria-expanded', 'false');
      anchor.removeAttribute('aria-controls');
    }
    this.#anchorWatch.disconnect();
    this.#request.set(null);
    this.#sub.set(null);
    this.#typeAhead.cancel();
  }

  /** Top-layer calls are optional: happy-dom has no popover API (web.md, conventions 11). */
  #show(panel: HTMLElement | null): void {
    if (panel && !panel.matches(':popover-open')) panel.showPopover?.();
  }

  #hide(panel: HTMLElement | null): void {
    if (panel?.matches(':popover-open')) panel.hidePopover?.();
  }

  #restoreFocus(request: NodeMenuRequest): void {
    (request.returnTo ?? request.anchor).focus();
  }

  /** The browser closed the panel (light dismiss, Escape): forget the open, keep focus sane. */
  readonly #onToggle = (e: ToggleEvent): void => {
    if (e.newState !== 'closed' || e.target !== this.#panel()) return;
    const request = this.#request();
    if (!request) return;
    this.#release();
    const active = document.activeElement;
    if (!active || active === document.body || this.contains(active)) this.#restoreFocus(request);
  };

  readonly #onKeydown = (event: KeyboardEvent): void => {
    const request = this.#request();
    if (!request) return;
    const target = event.target as HTMLElement;
    const inSub = this.#inSub(target);
    const items = this.#itemsOf(this.#levelOf(target));

    switch (event.key) {
      case 'Escape':
        event.preventDefault();
        event.stopPropagation();
        if (inSub) this.#closeSub();
        else this.close();
        return;
      case 'Tab':
        // mat-menu closes on Tab; focus continues from where the menu returns it.
        this.#closeAll();
        this.#restoreFocus(request);
        return;
      case 'ArrowRight': {
        const entry = target.closest<HTMLElement>('[aria-haspopup="menu"]');
        if (entry && !inSub) {
          event.preventDefault();
          this.#openSub(entry.dataset['entry'] === 'pause-after' ? 'after' : 'before', entry);
        }
        return;
      }
      case 'ArrowLeft':
        if (inSub) {
          event.preventDefault();
          this.#closeSub();
        }
        return;
      default:
        if (rovingKeydown(event, items, 'vertical')) return;
        this.#typeAhead.keydown(event);
    }
  };

  #jumpTo(query: string): void {
    const items = this.#itemsOf(this.#levelOf(document.activeElement));
    const current = items.findIndex((item) => item === document.activeElement);
    const index = typeAheadTarget(
      query,
      items.map((item) => item.textContent ?? ''),
      current,
    );
    if (index !== null) items[index]?.focus();
  }

  #panel(): HTMLElement | null {
    return this.querySelector<HTMLElement>('.r2m-node-menu__panel');
  }

  /** Whether `el` sits inside the open submenu. */
  #inSub(el: Element | null): boolean {
    return this.#sub() !== null && !!el?.closest('.r2m-node-menu__sub');
  }

  /** The menu level that holds `el`: the open submenu or the main panel. */
  #levelOf(el: Element | null): HTMLElement | null {
    return this.#inSub(el) ? this.#subPanel() : this.#panel();
  }

  #subPanel(): HTMLElement | null {
    const side = this.#sub();
    return side ? this.querySelector<HTMLElement>(`[data-sub="${side}"]`) : null;
  }

  /** The menu items of one level: the panel's own, not those of a nested submenu (or vice versa). */
  #itemsOf(level: HTMLElement | null): HTMLElement[] {
    if (!level) return [];
    return Array.from(level.querySelectorAll<HTMLElement>('[role="menuitem"]')).filter(
      (item) => item.closest('[role="menu"]') === level,
    );
  }

  #focusFirst(items: readonly HTMLElement[]): void {
    const [first] = items;
    if (first) first.focus();
  }
}
define('r2m-node-menu', NodeMenu);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-node-menu': NodeMenu;
  }
}
