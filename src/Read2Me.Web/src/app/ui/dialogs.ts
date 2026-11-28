import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { openDialog } from '@app/core/dialog';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { announceError } from './field';
import { icon } from './partials';

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /**
   * Red confirm with a delete icon: the change deletes, discards or replaces something the user
   * made or waited for (guidance-primitives spec §7). A change that loses nothing stays neutral.
   */
  destructive?: boolean;
}

/**
 * A change one more action cannot reverse (CONTEXT.md "permanent-change confirm"): what a button or
 * menu entry asks about before it runs. Each action has a pure builder in its feature folder that
 * returns one of these, or null when this particular call needs no confirm. `action` is the verb on
 * the button and in the title; `scope` says what changes, `cost` what reversing it would take.
 */
export interface PermanentChange {
  action: string;
  scope: string;
  cost: string;
  destructive: boolean;
}

/** The confirm a {@link PermanentChange} shows: "Action?", scope then cost, the verb on the button. */
export function permanentConfirmOptions(change: PermanentChange): ConfirmOptions {
  return {
    title: `${change.action}?`,
    message: `${change.scope} ${change.cost}`,
    confirmLabel: change.action,
    destructive: change.destructive,
  };
}

/** Asks about the change through {@link ConfirmService}; null needs no asking and is true at once. */
export async function confirmPermanent(change: PermanentChange | null): Promise<boolean> {
  if (change === null) return true;
  return use(ConfirmService).confirm(permanentConfirmOptions(change));
}

/** Confirmation dialog (design §7): resolves true only on the confirm button. */
export class ConfirmDialog extends R2mElement {
  data!: ConfirmOptions;

  protected template() {
    const d = this.data;
    // A destructive confirm focuses Cancel, so an Enter or Space straight after it opens keeps the
    // thing rather than deleting it (modern-web-guidance `dialog`).
    const focusCancel = d.destructive === true;
    return html`
      <h2 class="r2m-dialog__title">${d.title}</h2>
      <div class="r2m-dialog__content r2m-confirm-dialog__message">${d.message}</div>
      <div class="r2m-dialog__actions">
        <button
          type="button"
          ?autofocus=${focusCancel}
          class="r2m-button r2m-confirm-dialog__cancel"
          @click=${() => this.emit('r2m-close', false)}
        >
          ${d.cancelLabel ?? 'Cancel'}
        </button>
        <button
          type="button"
          ?autofocus=${!focusCancel}
          class="r2m-button r2m-button--filled ${d.destructive ? 'r2m-button--danger' : ''} r2m-confirm-dialog__confirm"
          @click=${() => this.emit('r2m-close', true)}
        >
          ${d.destructive ? icon('delete_forever') : nothing}
          ${d.confirmLabel ?? (d.destructive ? 'Delete' : 'OK')}
        </button>
      </div>
    `;
  }
}
define('r2m-confirm-dialog', ConfirmDialog);

export class ConfirmService {
  async confirm(options: ConfirmOptions): Promise<boolean> {
    const dialog = document.createElement('r2m-confirm-dialog');
    dialog.data = options;
    return (await openDialog<boolean>(dialog)) === true;
  }
}

export interface TextPromptOptions {
  title: string;
  label?: string;
  initial?: string;
  multiline?: boolean;
  required?: boolean;
  placeholder?: string;
  confirmLabel?: string;
  /** Static guidance shown under the field, which also describes the control. */
  hint?: string;
}

const PROMPT_HINT_ID = 'r2m-text-prompt-hint';
const PROMPT_ERROR_ID = 'r2m-text-prompt-error';

/**
 * One-field text prompt; resolves the trimmed text, or null on Cancel. A form: Enter in the
 * single-line field submits it natively (IME-safe), the confirm stays enabled, and a required
 * prompt submitted blank shows its error and keeps focus in the field. The error shows once the
 * field is blurred or the form submitted, never while typing, and clears as soon as it is fixed.
 */
export class TextPromptDialog extends R2mElement {
  data!: TextPromptOptions;
  readonly #value = signal('');
  readonly #touched = signal(false);
  readonly #error = computed(() =>
    this.data.required && this.#value().trim().length === 0
      ? `${this.data.label ?? 'This field'} is required`
      : null,
  );

  protected override connected(): void {
    this.#value.set(this.data.initial ?? '');
  }

  protected template() {
    const d = this.data;
    const onInput = (e: Event) => this.#value.set((e.target as HTMLInputElement).value);
    const onBlur = () => this.#touched.set(true);
    const error = this.#touched() ? this.#error() : null;
    const describedBy = error ? PROMPT_ERROR_ID : d.hint ? PROMPT_HINT_ID : nothing;
    const invalid = error ? 'true' : 'false';
    const announced = announceError(d.label ?? d.title);
    return html`
      <h2 class="r2m-dialog__title">${d.title}</h2>
      <form novalidate @submit=${(e: SubmitEvent) => this.#submit(e)}>
        <div class="r2m-dialog__content">
          <label class="r2m-field">
            ${d.label ? html`<span class="r2m-field__label">${d.label}</span>` : nothing}
            ${
              d.multiline
                ? html`<textarea
                    autofocus
                    class="r2m-text-prompt-dialog__input"
                    rows="3"
                    aria-describedby=${describedBy}
                    aria-invalid=${invalid}
                    placeholder=${d.placeholder ?? ''}
                    .value=${this.#value()}
                    @input=${onInput}
                    @blur=${onBlur}
                  ></textarea>`
                : html`<input
                    autofocus
                    type="text"
                    class="r2m-text-prompt-dialog__input"
                    aria-describedby=${describedBy}
                    aria-invalid=${invalid}
                    placeholder=${d.placeholder ?? ''}
                    .value=${this.#value()}
                    @input=${onInput}
                    @blur=${onBlur}
                  />`
            }
          </label>
          ${
            error
              ? html`<p class="r2m-field__error" id=${PROMPT_ERROR_ID} ${announced}>${error}</p>`
              : d.hint
                ? html`<p class="r2m-field__hint" id=${PROMPT_HINT_ID}>${d.hint}</p>`
                : nothing
          }
        </div>
        <div class="r2m-dialog__actions">
          <button
            type="button"
            class="r2m-button r2m-text-prompt-dialog__cancel"
            @click=${() => this.emit('r2m-close', null)}
          >
            Cancel
          </button>
          <button
            type="submit"
            class="r2m-button r2m-button--filled r2m-text-prompt-dialog__confirm"
          >
            ${d.confirmLabel ?? 'OK'}
          </button>
        </div>
      </form>
    `;
  }

  #submit(e: SubmitEvent): void {
    e.preventDefault();
    this.#touched.set(true);
    if (this.#error() === null) {
      this.emit('r2m-close', this.#value().trim());
      return;
    }
    this.querySelector<HTMLElement>('.r2m-text-prompt-dialog__input')?.focus();
  }
}
define('r2m-text-prompt-dialog', TextPromptDialog);

export class PromptService {
  async text(options: TextPromptOptions): Promise<string | null> {
    const dialog = document.createElement('r2m-text-prompt-dialog');
    dialog.data = options;
    return (await openDialog<string | null>(dialog)) ?? null;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-confirm-dialog': ConfirmDialog;
    'r2m-text-prompt-dialog': TextPromptDialog;
  }
}
