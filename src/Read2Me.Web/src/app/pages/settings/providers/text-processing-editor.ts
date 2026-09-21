import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TextStep } from '@app/api';
import {
  TO_SENTENCE_CASE_STEP,
  TextProcessingForm,
  ToSentenceCaseForm,
  addSubstitution,
  removeSubstitution,
  setStepEnabled,
  updateSubstitution,
} from './provider-form';

export type TextProcessingEdit = (text: TextProcessingForm) => TextProcessingForm;

/**
 * A TTS config's text processing (ticket 22): a checkbox per built-in step from the host's catalog
 * — sentence case opens its three options — and the config's own substitution rows. Controlled:
 * every edit leaves as a function of the owner's current state, so two edits that land before the
 * next render never overwrite each other.
 */
@Component({
  selector: 'app-text-processing-editor',
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatTooltipModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="text-steps">
      <h3 class="text-steps__title">Text processing</h3>
      @for (step of builtIns(); track step.stepId) {
        <mat-checkbox
          [attr.data-step]="step.stepId"
          [checked]="isEnabled(step.stepId)"
          [matTooltip]="step.description"
          matTooltipPosition="right"
          (change)="toggle(step.stepId, $event.checked)"
        >
          {{ step.label }}
        </mat-checkbox>
        @if (step.stepId === sentenceCaseStep && isEnabled(step.stepId)) {
          <div class="text-steps__options">
            <mat-checkbox
              data-option="paragraphEnabled"
              [checked]="text().toSentenceCase.paragraphEnabled"
              (change)="setSentenceCase({ paragraphEnabled: $event.checked })"
            >
              {{ optionLabel(step, 'paragraphEnabled', 'Normalise all-caps paragraphs') }}
            </mat-checkbox>
            <mat-checkbox
              data-option="wordEnabled"
              [checked]="text().toSentenceCase.wordEnabled"
              (change)="setSentenceCase({ wordEnabled: $event.checked })"
            >
              {{ optionLabel(step, 'wordEnabled', 'De-shout long all-caps words') }}
            </mat-checkbox>
            @if (text().toSentenceCase.wordEnabled) {
              <mat-form-field appearance="outline" class="text-steps__length">
                <mat-label>{{ optionLabel(step, 'wordMinLength', 'Minimum word length') }}</mat-label>
                <input
                  matInput
                  inputmode="numeric"
                  data-option="wordMinLength"
                  [value]="text().toSentenceCase.wordMinLength"
                  (input)="setSentenceCase({ wordMinLength: $any($event.target).value })"
                />
              </mat-form-field>
            }
          </div>
        }
      }
    </section>

    <section class="text-steps">
      <h3 class="text-steps__title">Substitutions</h3>
      @for (row of text().substitutions; track row.id) {
        <div class="text-steps__row" data-role="substitution">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>From</mat-label>
            <input
              matInput
              autocomplete="off"
              data-field="fromText"
              [value]="row.fromText"
              (input)="edit(row.id, { fromText: $any($event.target).value })"
            />
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>To</mat-label>
            <input
              matInput
              autocomplete="off"
              data-field="toText"
              [value]="row.toText"
              (input)="edit(row.id, { toText: $any($event.target).value })"
            />
          </mat-form-field>
          <mat-checkbox
            data-field="enabled"
            [checked]="isEnabled(row.id)"
            (change)="toggle(row.id, $event.checked)"
          >
            Enabled
          </mat-checkbox>
          <button
            mat-icon-button
            type="button"
            data-action="remove-substitution"
            aria-label="Remove substitution"
            (click)="remove(row.id)"
          >
            <mat-icon>delete</mat-icon>
          </button>
        </div>
      }
      <div>
        <button mat-button type="button" data-action="add-substitution" (click)="add()">
          <mat-icon>add</mat-icon>
          Add substitution
        </button>
      </div>
    </section>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-4);
    }
    .text-steps {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
    }
    .text-steps__title {
      margin: 0;
      font-size: var(--r2m-text-sm);
      font-weight: 600;
    }
    .text-steps__options {
      display: flex;
      flex-direction: column;
      margin-left: var(--r2m-space-6);
    }
    .text-steps__length {
      max-width: 200px;
      margin-top: var(--r2m-space-2);
    }
    .text-steps__row {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto auto;
      gap: var(--r2m-space-2);
      align-items: center;
    }
    @media (max-width: 600px) {
      .text-steps__row {
        grid-template-columns: minmax(0, 1fr) auto;
      }
    }
  `,
})
export class TextProcessingEditor {
  readonly text = input.required<TextProcessingForm>();
  /** The host's step catalog; the config's substitutions in it are edited as rows instead. */
  readonly steps = input.required<TextStep[]>();
  readonly textEdit = output<TextProcessingEdit>();

  protected readonly sentenceCaseStep = TO_SENTENCE_CASE_STEP;
  protected readonly builtIns = computed(() => this.steps().filter((s) => s.builtIn));

  protected isEnabled(stepId: string): boolean {
    return this.text().enabledStepIds.includes(stepId);
  }

  protected optionLabel(step: TextStep, key: string, fallback: string): string {
    return step.options?.find((o) => o.key === key)?.label ?? fallback;
  }

  protected toggle(stepId: string, enabled: boolean): void {
    this.textEdit.emit((text) => setStepEnabled(text, stepId, enabled));
  }

  protected setSentenceCase(change: Partial<ToSentenceCaseForm>): void {
    this.textEdit.emit((text) => ({
      ...text,
      toSentenceCase: { ...text.toSentenceCase, ...change },
    }));
  }

  protected add(): void {
    const id = crypto.randomUUID();
    this.textEdit.emit((text) => addSubstitution(text, id));
  }

  protected edit(id: string, change: { fromText?: string; toText?: string }): void {
    this.textEdit.emit((text) => updateSubstitution(text, id, change));
  }

  protected remove(id: string): void {
    this.textEdit.emit((text) => removeSubstitution(text, id));
  }
}
