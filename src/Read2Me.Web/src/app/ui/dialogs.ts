import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { openDialog } from '@app/core/dialog';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
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
    return html`
      <h2 class="r2m-dialog__title">${d.title}</h2>
      <div class="r2m-dialog__content r2m-confirm-dialog__message">${d.message}</div>
      <div class="r2m-dialog__actions">
        <button
          type="button"
          class="r2m-button r2m-confirm-dialog__cancel"
          @click=${() => this.emit('r2m-close', false)}
        >
          ${d.cancelLabel ?? 'Cancel'}
        </button>
        <button
          type="button"
          autofocus
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

/** One-field text prompt; resolves the trimmed text, or null on Cancel. */
export class TextPromptDialog extends R2mElement {
  data!: TextPromptOptions;
  readonly #value = signal('');
  readonly #valid = computed(() => !this.data.required || this.#value().trim().length > 0);

  protected override connected(): void {
    this.#value.set(this.data.initial ?? '');
  }

  protected template() {
    const d = this.data;
    const onInput = (e: Event) => this.#value.set((e.target as HTMLInputElement).value);
    const describedBy = d.hint ? PROMPT_HINT_ID : nothing;
    return html`
      <h2 class="r2m-dialog__title">${d.title}</h2>
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
                  placeholder=${d.placeholder ?? ''}
                  .value=${this.#value()}
                  @input=${onInput}
                ></textarea>`
              : html`<input
                  autofocus
                  type="text"
                  class="r2m-text-prompt-dialog__input"
                  aria-describedby=${describedBy}
                  placeholder=${d.placeholder ?? ''}
                  .value=${this.#value()}
                  @input=${onInput}
                  @keydown=${(e: KeyboardEvent) => e.key === 'Enter' && this.#submit()}
                />`
          }
        </label>
        ${d.hint ? html`<p class="r2m-field__hint" id=${PROMPT_HINT_ID}>${d.hint}</p>` : nothing}
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
          type="button"
          class="r2m-button r2m-button--filled r2m-text-prompt-dialog__confirm"
          ?disabled=${!this.#valid()}
          @click=${() => this.#submit()}
        >
          ${d.confirmLabel ?? 'OK'}
        </button>
      </div>
    `;
  }

  #submit(): void {
    if (this.#valid()) this.emit('r2m-close', this.#value().trim());
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
