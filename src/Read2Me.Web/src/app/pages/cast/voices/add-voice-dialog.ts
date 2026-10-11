import { html } from 'lit-html';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { signal } from '@app/core/signals';

export interface AddVoiceDialogData {
  /** The name the voice takes when the field is left blank: the character's name. */
  characterName: string;
}

export interface AddVoiceDialogResult {
  name: string;
  isGenerated: boolean;
}

/** Add voice: a name (default = the character) and the source, Reference Audio or Prompt. */
export class AddVoiceDialog extends R2mElement {
  data!: AddVoiceDialogData;

  readonly name = signal('');
  readonly isGenerated = signal(false);

  protected template() {
    return html`
      <h2 class="r2m-dialog__title">Add voice</h2>
      <div class="r2m-dialog__content add-voice">
        <label class="r2m-field add-voice__name">
          <span class="r2m-field__label">Voice name</span>
          <input
            type="text"
            autofocus
            placeholder=${this.data.characterName}
            .value=${this.name()}
            @input=${(e: Event) => this.name.set((e.target as HTMLInputElement).value)}
            @keydown=${(e: KeyboardEvent) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                this.submit();
              }
            }}
          />
        </label>
        <fieldset
          class="r2m-radio-group add-voice__source"
          role="radiogroup"
          aria-label="Voice source"
        >
          <label class="r2m-radio">
            <input
              type="radio"
              name="source"
              value="reference"
              .checked=${!this.isGenerated()}
              @change=${() => this.isGenerated.set(false)}
            />
            Reference audio
          </label>
          <label class="r2m-radio">
            <input
              type="radio"
              name="source"
              value="prompt"
              .checked=${this.isGenerated()}
              @change=${() => this.isGenerated.set(true)}
            />
            Prompt
          </label>
        </fieldset>
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', null)}>
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="add"
          @click=${() => this.submit()}
        >
          Add
        </button>
      </div>
    `;
  }

  submit(): void {
    const name = this.name().trim() || this.data.characterName;
    const result: AddVoiceDialogResult = { name, isGenerated: this.isGenerated() };
    this.emit('r2m-close', result);
  }
}
define('r2m-add-voice-dialog', AddVoiceDialog);

export async function openAddVoiceDialog(
  data: AddVoiceDialogData,
): Promise<AddVoiceDialogResult | null> {
  const dialog = document.createElement('r2m-add-voice-dialog');
  dialog.data = data;
  return (await openDialog<AddVoiceDialogResult | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-add-voice-dialog': AddVoiceDialog;
  }
}
