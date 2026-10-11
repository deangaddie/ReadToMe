import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { icon } from './partials';

/**
 * Click-to-edit text (design §7, §8): Enter saves, Escape cancels, blur saves. Emits `save` only
 * when the value actually changed and is valid (non-empty when `required`, within `maxLength`),
 * `cancelled` otherwise. The input is focused and selected on the render that opens it.
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

  readonly #editing = signal(false);
  readonly #draft = signal('');
  /** The input was just opened: focus and select it after that render. */
  #focusPending = false;

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
    if (!this.#focusPending) return;
    this.#focusPending = false;
    const input = this.querySelector<HTMLInputElement>('.r2m-inline-edit__input');
    input?.focus();
    input?.select();
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
        aria-invalid=${this.#valid() ? 'false' : 'true'}
        @input=${(e: Event) => this.#draft.set((e.target as HTMLInputElement).value)}
        @keydown=${this.#onKeydown}
        @blur=${() => this.commit()}
      />`;
    }
    return html`<button
      type="button"
      class="r2m-inline-edit__display ${value ? '' : 'r2m-inline-edit__display--empty'}"
      aria-label=${`Edit ${placeholder || 'value'}`}
      @click=${() => this.begin()}
    >
      <span class="r2m-inline-edit__text">${value || placeholder}</span>
      ${icon('edit', 'r2m-inline-edit__pencil')}
    </button>`;
  }

  readonly #onKeydown = (e: KeyboardEvent): void => {
    if (e.key === 'Enter') {
      e.preventDefault();
      this.commit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      this.abort();
    }
  };

  begin(): void {
    this.#draft.set(this.#value());
    this.#focusPending = true;
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
