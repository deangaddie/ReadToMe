import type { Block } from '@app/api';
import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { isComposing } from './keys';
import { gated, icon } from './partials';

/**
 * Click-to-edit text (design §7, §8): Enter saves, Escape cancels, blur saves. Emits `save` only
 * when the value actually changed and is valid (non-empty when `required`, within `maxLength`),
 * `cancelled` otherwise. It has no error message to describe an invalid draft with, so it sets no
 * `aria-invalid` either: an invalid draft just cancels. The input is focused and selected on the render that opens it. A
 * `block` (its Edit gate, such as viewing a History step) keeps it closed, its button off with
 * the gate's Blocked reason.
 */
export class InlineEdit extends R2mElement {
  readonly #value = signal('');
  get value() {
    return this.#value();
  }
  set value(value: string) {
    this.#value.set(value);
  }

  readonly #placeholder = signal('');
  get placeholder() {
    return this.#placeholder();
  }
  set placeholder(value: string) {
    this.#placeholder.set(value);
  }

  readonly #required = signal(false);
  get required() {
    return this.#required();
  }
  set required(value: boolean) {
    this.#required.set(value);
  }

  readonly #maxLength = signal<number | undefined>(undefined);
  get maxLength() {
    return this.#maxLength();
  }
  set maxLength(value: number | undefined) {
    this.#maxLength.set(value);
  }

  readonly #block = signal<Block | null>(null);
  get block() {
    return this.#block();
  }
  set block(value: Block | null) {
    this.#block.set(value);
  }

  readonly #editing = signal(false);
  readonly #draft = signal('');
  /**
   * What takes focus after the next render: the input just opened (its text selected), or the
   * display button a key just closed the edit back to, since the focused input leaves the DOM.
   */
  #focusTarget: 'input' | 'display' | null = null;

  readonly #valid = computed(() => {
    const text = this.#draft().trim();
    if (this.#required() && text.length === 0) return false;
    const max = this.#maxLength();
    return max === undefined || text.length <= max;
  });

  protected override connected(): void {
    this.classList.add('r2m-inline-edit');
    this.effect(() => this.classList.toggle('r2m-inline-edit--editing', this.#editing()));
  }

  protected override updated(): void {
    const target = this.#focusTarget;
    if (!target) return;
    this.#focusTarget = null;
    const el = this.querySelector<HTMLElement>(`.r2m-inline-edit__${target}`);
    el?.focus();
    if (el instanceof HTMLInputElement) el.select();
  }

  protected template() {
    const value = this.#value();
    const placeholder = this.#placeholder();
    if (this.#editing()) {
      const max = this.#maxLength();
      return html`<input
        class="r2m-inline-edit__input"
        type="text"
        .value=${this.#draft()}
        placeholder=${placeholder}
        maxlength=${max ?? nothing}
        @input=${(e: Event) => this.#draft.set((e.target as HTMLInputElement).value)}
        @keydown=${this.#onKeydown}
        @blur=${() => this.commit()}
      />`;
    }
    // The name carries the visible text as written, so voice control and screen readers get it.
    const editName = `Edit ${placeholder || 'value'}`;
    return gated(
      this.#block(),
      (gate) =>
        html`<button
          type="button"
          class="r2m-inline-edit__display ${value ? '' : 'r2m-inline-edit__display--empty'}"
          aria-label=${value ? `${editName}: ${value}` : editName}
          ${gate.attrs()}
          @click=${() => this.begin()}
        >
          <span class="r2m-inline-edit__text">${value || placeholder}</span>
          ${icon('edit', 'r2m-inline-edit__pencil')}
        </button>`,
    );
  }

  readonly #onKeydown = (e: KeyboardEvent): void => {
    if (e.key !== 'Enter' && e.key !== 'Escape') return;
    // Mid-composition both keys belong to the IME.
    if (isComposing(e)) return;
    e.preventDefault();
    this.#focusTarget = 'display';
    if (e.key === 'Enter') this.commit();
    else this.abort();
  };

  begin(): void {
    if (this.#block() !== null) return;
    this.#draft.set(this.#value());
    this.#focusTarget = 'input';
    this.#editing.set(true);
  }

  commit(): void {
    if (!this.#editing()) return;
    const text = this.#draft().trim();
    this.#editing.set(false);
    if (!this.#valid() || text === this.#value()) {
      this.emit('cancelled');
      return;
    }
    this.emit('save', text);
  }

  abort(): void {
    if (!this.#editing()) return;
    this.#editing.set(false);
    this.emit('cancelled');
  }
}
define('r2m-inline-edit', InlineEdit);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-inline-edit': InlineEdit;
  }
}
