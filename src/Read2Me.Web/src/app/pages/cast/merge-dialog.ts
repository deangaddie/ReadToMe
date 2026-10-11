import { html, nothing } from 'lit-html';
import { type CharacterSummaryDto, type Guid, VoicesApi } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import {
  type MergeForm,
  initialMergeForm,
  lostVoicesWarning,
  mergeCandidates,
  validateMerge,
} from './merge-options';

export interface MergeDialogData {
  folder: string;
  merged: CharacterSummaryDto;
  rows: readonly CharacterSummaryDto[];
}

export interface MergeChoice {
  survivorId: Guid;
  addNameAsAlias: boolean;
}

/**
 * Merge one character into another (research §4 "Merge"): survivor select, "add name as alias"
 * on by default, and a warning naming the voices the merged character takes with it. Closes with
 * the form to post, or nothing on Cancel.
 */
export class MergeDialog extends R2mElement {
  private readonly voices = use(VoicesApi);

  data!: MergeDialogData;

  private candidates: CharacterSummaryDto[] = [];
  readonly form = signal<MergeForm>({ survivorId: null, addNameAsAlias: true });
  private readonly voiceNames = signal<string[]>([]);
  readonly error = computed(() => validateMerge(this.form(), this.candidates, this.data.merged.id));
  readonly warning = computed(() => lostVoicesWarning(this.data.merged.name, this.voiceNames()));

  protected override connected(): void {
    this.candidates = mergeCandidates(this.data.rows, this.data.merged.id);
    this.form.set(initialMergeForm(this.candidates));
    // The roster row only counts voices; the warning names them.
    if (this.data.merged.voiceCount > 0) {
      void this.voices
        .list(this.data.folder, this.data.merged.id)
        .then((v) => this.voiceNames.set(v.voices.map((x) => x.name)))
        .catch(() =>
          this.voiceNames.set(Array<string>(this.data.merged.voiceCount).fill('(voice)')),
        );
    }
  }

  protected template() {
    const name = this.data.merged.name;
    const form = this.form();
    const warning = this.warning();
    const error = this.error();
    return html`
      <h2 class="r2m-dialog__title">Merge characters</h2>
      <div class="r2m-dialog__content merge">
        <p>
          Merge <strong>${name}</strong> into another character. All lines currently attributed to
          ${name} will be re-attributed to the survivor.
        </p>
        <label class="r2m-field merge__survivor">
          <span class="r2m-field__label">Merge into</span>
          <select
            name="survivor"
            autofocus
            .value=${form.survivorId ?? ''}
            @change=${(e: Event) =>
              this.patch({ survivorId: (e.target as HTMLSelectElement).value || null })}
          >
            ${this.candidates.map(
              (c) =>
                html`<option value=${c.id} ?selected=${c.id === form.survivorId}>
                  ${c.name}
                </option>`,
            )}
          </select>
        </label>
        <label class="merge__alias">
          <input
            type="checkbox"
            class="r2m-checkbox"
            name="addNameAsAlias"
            .checked=${form.addNameAsAlias}
            @change=${(e: Event) =>
              this.patch({ addNameAsAlias: (e.target as HTMLInputElement).checked })}
          />
          Add "${name}" as an alias of the survivor
        </label>
        ${warning ? html`<p class="merge__warning" role="alert">${warning}</p>` : nothing}
        ${error ? html`<p class="merge__error">${error}</p>` : nothing}
      </div>
      <div class="r2m-dialog__actions">
        <button
          type="button"
          class="r2m-button merge__cancel"
          @click=${() => this.emit('r2m-close', null)}
        >
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled merge__submit"
          ?disabled=${error !== null}
          @click=${() => this.submit()}
        >
          Merge
        </button>
      </div>
    `;
  }

  patch(change: Partial<MergeForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  submit(): void {
    const form = this.form();
    if (this.error() === null && form.survivorId) {
      const choice: MergeChoice = {
        survivorId: form.survivorId,
        addNameAsAlias: form.addNameAsAlias,
      };
      this.emit('r2m-close', choice);
    }
  }
}
define('r2m-merge-dialog', MergeDialog);

/** Opens the merge dialog; resolves the survivor choice, or null when cancelled. */
export async function openMergeDialog(data: MergeDialogData): Promise<MergeChoice | null> {
  const dialog = document.createElement('r2m-merge-dialog');
  dialog.data = data;
  return (await openDialog<MergeChoice | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-merge-dialog': MergeDialog;
  }
}
