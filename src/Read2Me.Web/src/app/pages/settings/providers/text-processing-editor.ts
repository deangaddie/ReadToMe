import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { TextStepDto } from '@app/api';
import { controlValue, field } from '@app/ui/field';
import { icon } from '@app/ui/partials';
import {
  TO_SENTENCE_CASE_STEP,
  type TextProcessingForm,
  type ToSentenceCaseForm,
  addSubstitution,
  removeSubstitution,
  setStepEnabled,
  updateSubstitution,
} from './provider-form';

export type TextProcessingEdit = (text: TextProcessingForm) => TextProcessingForm;

export interface TextProcessingEditorOptions {
  text: TextProcessingForm;
  /** The host's step catalog; the config's substitutions in it are edited as rows instead. */
  steps: TextStepDto[];
  onEdit: (edit: TextProcessingEdit) => void;
}

const checkedOf = (e: Event) => (e.target as HTMLInputElement).checked;

/** One of a substitution row's two texts, labelled above it; the row's id keeps the ids unique. */
function substitutionText(
  rowId: string,
  key: 'fromText' | 'toText',
  label: string,
  value: string,
  onEdit: TextProcessingEditorOptions['onEdit'],
) {
  return field(
    { id: `substitution-${rowId}-${key}`, label },
    (a) =>
      html`<input
        id=${a.id}
        aria-describedby=${a.describedBy}
        aria-invalid=${a.invalid}
        type="text"
        autocomplete="off"
        data-field=${key}
        .value=${value}
        @input=${(e: Event) => onEdit((t) => updateSubstitution(t, rowId, { [key]: controlValue(e) }))}
      />`,
  );
}

/**
 * A TTS config's text processing (native-web 35): a checkbox per built-in step from the host's catalog
 * — sentence case opens its three options — and the config's own substitution rows. Controlled,
 * so a partial: every edit leaves as a function of the owner's current state, so two edits that
 * land before the next render never overwrite each other.
 */
export function textProcessingEditor(o: TextProcessingEditorOptions) {
  const { text, onEdit } = o;
  const isEnabled = (stepId: string) => text.enabledStepIds.includes(stepId);
  const toggle = (stepId: string, enabled: boolean) =>
    onEdit((t) => setStepEnabled(t, stepId, enabled));
  const setSentenceCase = (change: Partial<ToSentenceCaseForm>) =>
    onEdit((t) => ({ ...t, toSentenceCase: { ...t.toSentenceCase, ...change } }));
  const optionLabel = (step: TextStepDto, key: string, fallback: string) =>
    step.options?.find((opt) => opt.key === key)?.label ?? fallback;

  return html`<div class="text-processing">
    <section class="text-steps">
      <h3 class="text-steps__title">Text processing</h3>
      ${o.steps
        .filter((s) => s.builtIn)
        .map(
          (step) => html`
            <label class="r2m-checkbox" data-step=${step.stepId} data-tooltip=${step.description}>
              <input
                type="checkbox"
                .checked=${isEnabled(step.stepId)}
                @change=${(e: Event) => toggle(step.stepId, checkedOf(e))}
              />
              ${step.label}
            </label>
            ${
              step.stepId === TO_SENTENCE_CASE_STEP && isEnabled(step.stepId)
                ? html`<div class="text-steps__options">
                    <label class="r2m-checkbox">
                      <input
                        type="checkbox"
                        data-option="paragraphEnabled"
                        .checked=${text.toSentenceCase.paragraphEnabled}
                        @change=${(e: Event) => setSentenceCase({ paragraphEnabled: checkedOf(e) })}
                      />
                      ${optionLabel(step, 'paragraphEnabled', 'Normalise all-caps paragraphs')}
                    </label>
                    <label class="r2m-checkbox">
                      <input
                        type="checkbox"
                        data-option="wordEnabled"
                        .checked=${text.toSentenceCase.wordEnabled}
                        @change=${(e: Event) => setSentenceCase({ wordEnabled: checkedOf(e) })}
                      />
                      ${optionLabel(step, 'wordEnabled', 'De-shout long all-caps words')}
                    </label>
                    ${
                      text.toSentenceCase.wordEnabled
                        ? field(
                            {
                              id: 'tts-word-min-length',
                              label: optionLabel(step, 'wordMinLength', 'Minimum word length'),
                              cls: 'text-steps__length',
                            },
                            (a) =>
                              html`<input
                                id=${a.id}
                                aria-describedby=${a.describedBy}
                                aria-invalid=${a.invalid}
                                type="text"
                                inputmode="numeric"
                                autocomplete="off"
                                data-option="wordMinLength"
                                .value=${text.toSentenceCase.wordMinLength}
                                @input=${(e: Event) => setSentenceCase({ wordMinLength: controlValue(e) })}
                              />`,
                          )
                        : nothing
                    }
                  </div>`
                : nothing
            }
          `,
        )}
    </section>

    <section class="text-steps">
      <h3 class="text-steps__title">Substitutions</h3>
      ${repeat(
        text.substitutions,
        (row) => row.id,
        (row) =>
          html`<div class="text-steps__row" data-role="substitution">
            ${substitutionText(row.id, 'fromText', 'From', row.fromText, onEdit)}
            ${substitutionText(row.id, 'toText', 'To', row.toText, onEdit)}
            <label class="r2m-checkbox" data-field="enabled">
              <input
                type="checkbox"
                .checked=${isEnabled(row.id)}
                @change=${(e: Event) => toggle(row.id, checkedOf(e))}
              />
              Enabled
            </label>
            <button
              type="button"
              class="r2m-icon-button"
              data-action="remove-substitution"
              aria-label="Remove substitution"
              @click=${() => onEdit((t) => removeSubstitution(t, row.id))}
            >
              ${icon('delete')}
            </button>
          </div>`,
      )}
      <div>
        <button
          type="button"
          class="r2m-button"
          data-action="add-substitution"
          @click=${() => {
            const id = crypto.randomUUID();
            onEdit((t) => addSubstitution(t, id));
          }}
        >
          ${icon('add')} Add substitution
        </button>
      </div>
    </section>
  </div>`;
}
