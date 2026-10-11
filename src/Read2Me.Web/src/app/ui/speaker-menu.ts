import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { speakerHue } from '@app/shared/speaker-color';
import { icon } from './partials';

export interface SpeakerRosterEntry {
  id: string;
  name: string;
  isNarrator?: boolean;
  aliases?: string[];
}

let nextId = 0;

/**
 * Speaker picker panel (design §7): roster search, narrator first, Clear speaker, New character…
 * Emits `pick` (id), `clear` and `create` (search text). A combobox: focus stays in the search box
 * and `aria-activedescendant` moves the highlight — the CDK ActiveDescendantKeyManager's job,
 * done here by one index signal. Because the filtered list is computed synchronously, Enter can
 * never beat a re-render, which the Angular version had to guard against.
 */
export class SpeakerMenu extends R2mElement {
  #roster = signal<readonly SpeakerRosterEntry[]>([]);
  get roster() {
    return this.#roster();
  }
  set roster(value: readonly SpeakerRosterEntry[]) {
    this.#roster.set(value);
  }

  #selectedId = signal<string | undefined>(undefined);
  get selectedId() {
    return this.#selectedId();
  }
  set selectedId(value: string | undefined) {
    this.#selectedId.set(value);
  }

  #allowClear = signal(true);
  get allowClear() {
    return this.#allowClear();
  }
  set allowClear(value: boolean) {
    this.#allowClear.set(value);
  }

  #allowCreate = signal(true);
  get allowCreate() {
    return this.#allowCreate();
  }
  set allowCreate(value: boolean) {
    this.#allowCreate.set(value);
  }

  readonly #uid = `r2m-speaker-menu-${nextId++}`;
  readonly #query = signal('');
  /** Index into `filtered`, -1 for none: an empty search highlights nobody. */
  readonly #active = signal(-1);

  readonly #filtered = computed(() => {
    const q = this.#query().trim().toLowerCase();
    return this.#roster()
      .filter(
        (e) =>
          !q ||
          e.name.toLowerCase().includes(q) ||
          (e.aliases ?? []).some((a) => a.toLowerCase().includes(q)),
      )
      .sort(
        (a, b) =>
          Number(!!b.isNarrator) - Number(!!a.isNarrator) ||
          a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
      );
  });

  protected override connected(): void {
    this.classList.add('r2m-speaker-menu');
    this.addEventListener('keydown', this.#onKeydown);
    this.onDisconnect(() => this.removeEventListener('keydown', this.#onKeydown));
  }

  /** Puts the cursor in the search box (the chip calls it on open). */
  async focusSearch(): Promise<void> {
    await this.rendered();
    this.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')?.focus();
  }

  protected template() {
    const filtered = this.#filtered();
    const active = this.#active();
    const query = this.#query().trim();
    return html`
      <label class="r2m-speaker-menu__search">
        ${icon('search')}
        <input
          type="text"
          role="combobox"
          class="r2m-speaker-menu__input"
          placeholder="Search characters"
          autocomplete="off"
          aria-label="Search characters"
          aria-controls="${this.#uid}-list"
          aria-expanded="true"
          aria-activedescendant=${active >= 0 ? `${this.#uid}-${active}` : nothing}
          .value=${this.#query()}
          @input=${(e: Event) => this.#onSearch((e.target as HTMLInputElement).value)}
        />
      </label>
      <div
        class="r2m-speaker-menu__list"
        role="listbox"
        id="${this.#uid}-list"
        aria-label="Characters"
      >
        ${
          filtered.length === 0
            ? html`<p class="r2m-speaker-menu__empty">No matches</p>`
            : repeat(
                filtered,
                (entry) => entry.id,
                (entry, i) =>
                  html`<button
                    type="button"
                    role="option"
                    tabindex="-1"
                    id="${this.#uid}-${i}"
                    class="r2m-speaker-menu__row ${entry.id === this.#selectedId() ? 'r2m-speaker-menu__row--selected' : ''} ${i === active ? 'r2m-speaker-menu__row--active' : ''}"
                    aria-selected=${entry.id === this.#selectedId()}
                    style="--r2m-speaker-hue: ${speakerHue(entry.id)}"
                    @click=${() => this.#output('pick', entry.id)}
                  >
                    <span class="r2m-speaker-menu__dot" aria-hidden="true"></span>
                    <span class="r2m-speaker-menu__name">${entry.name}</span>
                    ${entry.isNarrator ? icon('auto_stories', 'r2m-speaker-menu__narrator') : nothing}
                  </button>`,
              )
        }
      </div>
      ${
        this.allowClear || this.allowCreate
          ? html`<div class="r2m-speaker-menu__footer">
              ${
                this.allowClear
                  ? html`<button
                      type="button"
                      class="r2m-speaker-menu__action"
                      @click=${() => this.#output('clear')}
                    >
                      ${icon('person_off')}<span>Clear speaker</span>
                    </button>`
                  : nothing
              }
              ${
                this.allowCreate
                  ? html`<button
                      type="button"
                      class="r2m-speaker-menu__action"
                      @click=${() => this.#output('create', query)}
                    >
                      ${icon('person_add')}<span>New character${query ? ` “${query}”` : '…'}</span>
                    </button>`
                  : nothing
              }
            </div>`
          : nothing
      }
    `;
  }

  /** The chip that hosts the menu re-exposes these, so they bubble. */
  #output(type: 'pick' | 'clear' | 'create', detail?: string): void {
    this.emit(type, detail, { bubbles: true });
  }

  #onSearch(value: string): void {
    this.#query.set(value);
    this.#active.set(value.trim() && this.#filtered().length ? 0 : -1);
  }

  #onKeydown = (event: KeyboardEvent): void => {
    const matches = this.#filtered();
    // Enter on a footer button is that button's own click.
    if (event.key === 'Enter' && !(event.target instanceof HTMLButtonElement)) {
      const target = matches[this.#active()];
      const query = this.#query().trim();
      if (target) {
        event.preventDefault();
        this.#output('pick', target.id);
      } else if (!matches.length && query && this.allowCreate) {
        event.preventDefault();
        this.#output('create', query);
      }
      return;
    }
    if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && matches.length) {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      const from = this.#active();
      const next =
        from < 0
          ? step > 0
            ? 0
            : matches.length - 1
          : (from + step + matches.length) % matches.length;
      this.#active.set(next);
      this.querySelector(`#${this.#uid}-${next}`)?.scrollIntoView({ block: 'nearest' });
    }
  };
}
define('r2m-speaker-menu', SpeakerMenu);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-speaker-menu': SpeakerMenu;
  }
}
