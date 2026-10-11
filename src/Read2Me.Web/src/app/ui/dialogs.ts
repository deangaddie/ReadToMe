import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { openDialog } from '@app/core/dialog';
import { computed, signal } from '@app/core/signals';
import { icon } from './partials';

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Red confirm with a delete icon — every delete, reread, clear, regenerate-all (design §8). */
  destructive?: boolean;
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
}

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
                  placeholder=${d.placeholder ?? ''}
                  .value=${this.#value()}
                  @input=${onInput}
                ></textarea>`
              : html`<input
                  autofocus
                  type="text"
                  class="r2m-text-prompt-dialog__input"
                  placeholder=${d.placeholder ?? ''}
                  .value=${this.#value()}
                  @input=${onInput}
                  @keydown=${(e: KeyboardEvent) => e.key === 'Enter' && this.#submit()}
                />`
          }
        </label>
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
