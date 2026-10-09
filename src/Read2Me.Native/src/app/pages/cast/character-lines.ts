import { html, nothing } from 'lit-html';
import type { CharacterLineDto } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import type { MeasuredList } from '@app/ui/measured-list';
import { icon } from '@app/ui/partials';
import '@app/ui/measured-list';
import './line-context';

/** Every row is one line high, so the list can virtualise over thousands of lines. */
export const LINE_ROW_HEIGHT = 36;

/**
 * A character's lines (research §4 "Lines"): a virtual list of single-line rows over
 * `<r2m-measured-list>`. Clicking the text emits `open` with the line (the detail opens the reader
 * there); the chevron expands one line's context beneath the list (one at a time, so the rows
 * keep their fixed height).
 */
export class CharacterLines extends R2mElement {
  readonly #folder = signal('');
  get folder() {
    return this.#folder();
  }
  set folder(value: string) {
    this.#folder.set(value);
  }

  readonly #lines = signal<readonly CharacterLineDto[]>([]);
  get lines() {
    return this.#lines();
  }
  set lines(value: readonly CharacterLineDto[]) {
    this.#lines.set(value);
  }

  readonly #loading = signal(false);
  get loading() {
    return this.#loading();
  }
  set loading(value: boolean) {
    this.#loading.set(value);
  }

  readonly expandedId = signal<string | null>(null);

  readonly expanded = computed(() => {
    const id = this.expandedId();
    return id === null ? null : (this.#lines().find((l) => l.itemId === id) ?? null);
  });

  /** Up to ten rows tall, then scrolls. */
  private readonly viewportHeight = computed(
    () => Math.min(this.#lines().length, 10) * LINE_ROW_HEIGHT + 2,
  );

  protected override connected(): void {
    this.classList.add('character-lines');
  }

  protected template() {
    const lines = this.#lines();
    if (lines.length === 0) {
      return html`<p class="character-lines__empty">
        ${this.#loading() ? 'Loading lines…' : 'No lines attributed to this character.'}
      </p>`;
    }
    const expanded = this.expanded();
    return html`
      <r2m-measured-list
        class="character-lines__viewport"
        style=${`height: ${this.viewportHeight()}px`}
        .key=${(line: CharacterLineDto) => line.itemId}
        .row=${(line: CharacterLineDto) => this.row(line)}
        .items=${lines}
      ></r2m-measured-list>
      ${
        expanded
          ? html`<div class="character-lines__context">
              <div class="character-lines__context-line">${expanded.text}</div>
              <r2m-line-context .folder=${this.#folder()} .line=${expanded}></r2m-line-context>
            </div>`
          : nothing
      }
    `;
  }

  /** Runs inside the list's effect: reading `expandedId` there re-renders the visible rows only. */
  private row(line: CharacterLineDto) {
    const open = this.expandedId() === line.itemId;
    return html`<div
      class="character-lines__row ${open ? 'character-lines__row--expanded' : ''}"
      data-item-id=${line.itemId}
    >
      <button
        type="button"
        class="r2m-icon-button character-lines__toggle"
        aria-expanded=${open ? 'true' : 'false'}
        aria-label=${open ? 'Hide context' : 'Show context'}
        @click=${() => this.toggle(line)}
      >
        ${icon(open ? 'expand_less' : 'expand_more')}
      </button>
      <button
        type="button"
        class="character-lines__text"
        data-tooltip="Open in the reader"
        @click=${() => this.emit('open', line)}
      >
        ${line.text}
      </button>
    </div>`;
  }

  toggle(line: CharacterLineDto): void {
    this.expandedId.update((id) => (id === line.itemId ? null : line.itemId));
  }

  /** The list, for a spec or a caller that wants to scroll it. */
  get list(): MeasuredList<CharacterLineDto> | null {
    return this.querySelector<MeasuredList<CharacterLineDto>>('r2m-measured-list');
  }
}
define('r2m-character-lines', CharacterLines);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-character-lines': CharacterLines;
  }
}
