import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { closeMenuOf, menuKeys } from './menu';
import { icon, statusChip } from './partials';

export interface ConfigListItem {
  id: string;
  name: string;
  subtitle?: string;
  isActive?: boolean;
  badge?: string;
}

let instances = 0;

/**
 * Master list of the settings master–detail template (design §6.4, §7): searchable rows with an
 * Active marker and a per-row `role=menu` popover (Make active / Duplicate / Delete), plus New.
 * An element because it owns the search text. Outputs: `pick` (not `select`, which a text
 * selection in the search box would also fire), `create`, `make-active`, `duplicate`, `delete`,
 * each with the item id as detail. The rows carry `data-overflows` while they have something to
 * scroll, the only time they may hold the wheel back from the page.
 */
export class ConfigList extends R2mElement {
  readonly #uid = ++instances;

  readonly #items = signal<ConfigListItem[]>([]);
  get items() {
    return this.#items();
  }
  set items(value: ConfigListItem[]) {
    this.#items.set(value);
  }

  readonly #selectedId = signal<string | null>(null);
  get selectedId() {
    return this.#selectedId();
  }
  set selectedId(value: string | null) {
    this.#selectedId.set(value);
  }

  readonly #busy = signal(false);
  get busy() {
    return this.#busy();
  }
  set busy(value: boolean) {
    this.#busy.set(value);
  }

  /** What the area calls its selected config: "Active" for providers, "Default" for LLM. */
  readonly #activeLabel = signal('Active');
  get activeLabel() {
    return this.#activeLabel();
  }
  set activeLabel(value: string) {
    this.#activeLabel.set(value);
  }

  readonly #query = signal('');

  readonly filtered = computed(() => {
    const q = this.#query().trim().toLowerCase();
    if (!q) return this.#items();
    return this.#items().filter(
      (i) => i.name.toLowerCase().includes(q) || (i.subtitle ?? '').toLowerCase().includes(q),
    );
  });

  protected override connected(): void {
    this.classList.add('r2m-config-list');
    // A render changes what the rows hold; a resize changes how much of it shows.
    const resize = new ResizeObserver(() => this.markOverflow());
    resize.observe(this);
    this.onDisconnect(() => resize.disconnect());
  }

  protected override updated(): void {
    this.markOverflow();
  }

  /** CSS can't say "only while it scrolls": `overscroll-behavior` also holds a list with nothing to scroll. */
  private markOverflow(): void {
    const rows = this.querySelector('.r2m-config-list__rows');
    rows?.toggleAttribute('data-overflows', rows.scrollHeight > rows.clientHeight);
  }

  protected template() {
    const busy = this.#busy();
    const filtered = this.filtered();
    // Read here, not inside the repeat callback: a lazy directive runs outside the element's effect.
    const selectedId = this.#selectedId();
    const activeLabel = this.#activeLabel();
    return html`
      <header class="r2m-config-list__head">
        <search class="r2m-config-list__filter" aria-label="Filter configurations">
          <label class="r2m-config-list__search">
            ${icon('search')}
            <input
              type="search"
              placeholder="Search"
              aria-label="Search configurations"
              .value=${this.#query()}
              @input=${(e: Event) => this.#query.set((e.target as HTMLInputElement).value)}
            />
          </label>
        </search>
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          ?disabled=${busy}
          @click=${() => this.emit('create')}
        >
          ${icon('add')} New
        </button>
      </header>

      ${
        filtered.length === 0
          ? html`<p class="r2m-config-list__empty">
              ${this.#items().length === 0 ? 'No configurations yet.' : 'Nothing matches your search.'}
            </p>`
          : html`<ul class="r2m-config-list__rows" role="listbox" aria-label="Configurations">
              ${repeat(
                filtered,
                (item) => item.id,
                (item) => this.row(item, busy, selectedId, activeLabel),
              )}
            </ul>`
      }
    `;
  }

  private row(item: ConfigListItem, busy: boolean, selectedId: string | null, activeLabel: string) {
    const selected = item.id === selectedId;
    const menuId = `r2m-config-list-${this.#uid}-${item.id}`;
    const act = (type: string) => (e: Event) => {
      closeMenuOf(e);
      this.emit(type, item.id);
    };
    return html`<li
      class="r2m-config-list__row ${selected ? 'r2m-config-list__row--selected' : ''}"
      role="option"
      aria-selected=${selected ? 'true' : 'false'}
    >
      <button
        type="button"
        class="r2m-config-list__main"
        @click=${() => this.emit('pick', item.id)}
      >
        <span class="r2m-config-list__name">${item.name}</span>
        ${item.subtitle ? html`<span class="r2m-config-list__subtitle">${item.subtitle}</span>` : nothing}
      </button>
      ${item.badge ? html`<span class="r2m-config-list__badge">${item.badge}</span>` : nothing}
      ${item.isActive ? statusChip({ status: 'ok', label: activeLabel, compact: true }) : nothing}
      <button
        type="button"
        class="r2m-icon-button r2m-config-list__more"
        popovertarget=${menuId}
        aria-haspopup="menu"
        ?disabled=${busy}
        aria-label=${`Actions for ${item.name}`}
      >
        ${icon('more_vert')}
      </button>
      <div
        id=${menuId}
        popover
        class="r2m-menu"
        role="menu"
        @keydown=${menuKeys.keydown}
        @toggle=${menuKeys.toggle}
        aria-label=${`Actions for ${item.name}`}
      >
        <button
          type="button"
          class="r2m-menu__item"
          role="menuitem"
          ?disabled=${item.isActive}
          @click=${act('make-active')}
        >
          ${icon('check_circle')}<span>Make ${activeLabel.toLowerCase()}</span>
        </button>
        <button type="button" class="r2m-menu__item" role="menuitem" @click=${act('duplicate')}>
          ${icon('content_copy')}<span>Duplicate</span>
        </button>
        <button
          type="button"
          class="r2m-menu__item r2m-menu__item--destructive r2m-config-list__delete"
          role="menuitem"
          @click=${act('delete')}
        >
          ${icon('delete')}<span>Delete</span>
        </button>
      </div>
    </li>`;
  }
}
define('r2m-config-list', ConfigList);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-config-list': ConfigList;
  }
}
