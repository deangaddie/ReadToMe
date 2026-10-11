import { html, nothing } from 'lit-html';
import type { ManualImportRequest, SplitRuleMode } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import {
  DEFAULT_MANUAL_REREAD_FORM,
  type LevelKey,
  type ManualRereadForm,
  SPLIT_MODES,
  activeLevels,
  toManualImportRequest,
  validateManualReread,
} from './manual-reread-form';
import manualRereadCss from './manual-reread-dialog.css' with { type: 'text' };

adoptStyles(manualRereadCss);

const LEVEL_HEADING: Record<LevelKey, string> = {
  volume: 'Volume detection',
  part: 'Part detection',
  chapter: 'Chapter detection',
};

const PREFIX_HINT: Record<LevelKey, string> = {
  volume: 'e.g. Volume, Book',
  part: 'e.g. Part',
  chapter: 'e.g. Chapter',
};

/**
 * Manual reread (research §3 ManualRereadDialog): structure switches, then one detection rule per
 * active level. Closes with the request to send, or null on Cancel. Validation is the pure form's.
 * The switches are `role=switch` checkboxes (`.r2m-switch`) and each level's modes a native radio
 * group (`.r2m-radio-group`), the §5.3 forms for mat-slide-toggle and mat-radio-group.
 */
export class ManualRereadDialog extends R2mElement {
  readonly #form = signal<ManualRereadForm>({ ...DEFAULT_MANUAL_REREAD_FORM });
  /** Shown after the first failed submit, then live. */
  readonly #submitted = signal(false);
  readonly #levels = computed(() => activeLevels(this.#form()));
  readonly #error = computed(() => (this.#submitted() ? validateManualReread(this.#form()) : null));

  protected override connected(): void {
    this.classList.add('manual');
  }

  protected template() {
    const form = this.#form();
    const error = this.#error();
    return html`
      <h2 class="r2m-dialog__title">Manual reread</h2>
      <div class="r2m-dialog__content manual__content">
        <p class="manual__hint">
          Re-splits the source file by the rules below. Existing chapters, attribution and audio are
          replaced.
        </p>

        <h3 class="manual__heading">Book structure</h3>
        <label class="r2m-switch manual__switch">
          <input
            type="checkbox"
            role="switch"
            name="hasVolumes"
            .checked=${form.hasVolumes}
            @change=${(e: Event) =>
              this.#patch({ hasVolumes: (e.target as HTMLInputElement).checked })}
          />
          Book has multiple volumes
        </label>
        <label class="r2m-switch manual__switch">
          <input
            type="checkbox"
            role="switch"
            name="hasParts"
            .checked=${form.hasParts}
            @change=${(e: Event) => this.#patch({ hasParts: (e.target as HTMLInputElement).checked })}
          />
          ${form.hasVolumes ? 'Each volume has multiple parts' : 'Book has multiple parts'}
        </label>

        ${this.#levels().map((level) => this.#levelRule(level, form))}
        ${error ? html`<p class="manual__error" role="alert">${error}</p>` : nothing}
      </div>
      <div class="r2m-dialog__actions">
        <button
          type="button"
          class="r2m-button manual__cancel"
          @click=${() => this.emit('r2m-close', null)}
        >
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled manual__submit"
          @click=${() => this.#submit()}
        >
          Reread
        </button>
      </div>
    `;
  }

  #levelRule(level: LevelKey, form: ManualRereadForm) {
    const rule = form[level];
    return html`<h3 class="manual__heading">${LEVEL_HEADING[level]}</h3>
      <fieldset
        class="r2m-radio-group manual__modes"
        role="radiogroup"
        aria-label=${LEVEL_HEADING[level]}
      >
        ${SPLIT_MODES.map(
          (m) =>
            html`<label class="r2m-radio">
              <input
                type="radio"
                name="mode-${level}"
                .value=${m.mode}
                .checked=${rule.mode === m.mode}
                @change=${() => this.#setMode(level, m.mode)}
              />
              ${m.label}
            </label>`,
        )}
      </fieldset>
      ${
        rule.mode === 'Prefix'
          ? html`<label class="r2m-field manual__prefix">
              <span class="r2m-field__label">Prefix (${PREFIX_HINT[level]})</span>
              <input
                type="text"
                data-level=${level}
                .value=${rule.prefix}
                @input=${(e: Event) => this.#setPrefix(level, (e.target as HTMLInputElement).value)}
              />
            </label>`
          : nothing
      }`;
  }

  #patch(changes: Partial<ManualRereadForm>): void {
    this.#form.update((f) => ({ ...f, ...changes }));
  }

  #setMode(level: LevelKey, mode: SplitRuleMode): void {
    this.#form.update((f) => ({ ...f, [level]: { ...f[level], mode } }));
  }

  #setPrefix(level: LevelKey, prefix: string): void {
    this.#form.update((f) => ({ ...f, [level]: { ...f[level], prefix } }));
  }

  #submit(): void {
    this.#submitted.set(true);
    if (validateManualReread(this.#form())) return;
    this.emit('r2m-close', toManualImportRequest(this.#form()));
  }
}
define('r2m-manual-reread-dialog', ManualRereadDialog);

/** Opens the dialog; resolves the request to send, or null when cancelled. */
export async function openManualRereadDialog(): Promise<ManualImportRequest | null> {
  const dialog = document.createElement('r2m-manual-reread-dialog');
  return (await openDialog<ManualImportRequest | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-manual-reread-dialog': ManualRereadDialog;
  }
}
