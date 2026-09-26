import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import {
  AttributionPromptStyle,
  LlmApiType,
  LlmServerConfig,
  LlmSettingsApi,
  toApiError,
} from '@app/api';
import { ConfigEditorFrame } from '@app/ui/config-editor-frame/config-editor-frame';
import { ToastService } from '@app/ui/toast/toast.service';
import { ManagedServiceStatus } from '../shared/managed-service-status';
import {
  API_TYPE_LABELS,
  EMPTY_LLM_FORM,
  LlmConfigForm,
  buildLlmConfig,
  sameLlmForm,
  validateLlmForm,
} from './llm-config-form';
import { LlmSettingsStore } from './llm-settings-store';

/** What the editor is editing: a saved config (`id` > 0) or a new one, which may start pre-filled. */
export interface LlmEditTarget {
  id: number;
  /** The saved state; Cancel returns here and dirty is measured against it. */
  baseline: LlmConfigForm;
  /** The opening draft — differs from `baseline` for a duplicate. */
  draft: LlmConfigForm;
}

const NUMERIC_FIELDS = [
  { key: 'temperature', label: 'Temperature' },
  { key: 'topP', label: 'Top P' },
  { key: 'maxTokens', label: 'Max tokens' },
  { key: 'frequencyPenalty', label: 'Frequency penalty' },
  { key: 'presencePenalty', label: 'Presence penalty' },
] as const;

/**
 * The in-place typed editor for one LLM config (ticket 21): Blazor's `LlmServerConfigDialog` fields
 * and validation messages inside the settings editor frame, plus the managed-container status for
 * the config's base URL.
 */
@Component({
  selector: 'app-llm-config-editor',
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    ConfigEditorFrame,
    ManagedServiceStatus,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-config-editor-frame
      [title]="target().id ? target().baseline.name : 'New configuration'"
      [subtitle]="target().id ? undefined : 'Saved configurations appear in the list.'"
      [dirty]="dirty()"
      [saving]="saving()"
      (save)="save()"
      (cancel)="reset()"
    >
      <div class="llm-editor">
        <!-- Follows the draft, not the saved config: a duplicate or a retyped URL shows its container. -->
        <app-managed-service-status [baseUrl]="form().baseUrl" />

        <mat-form-field appearance="outline">
          <mat-label>Name</mat-label>
          <input
            matInput
            required
            autocomplete="off"
            data-field="name"
            [value]="form().name"
            (input)="set('name', $any($event.target).value)"
          />
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>API type</mat-label>
          <mat-select [value]="form().apiType" (valueChange)="set('apiType', $event)">
            @for (type of apiTypes; track type.value) {
              <mat-option [value]="type.value">{{ type.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>Base URL</mat-label>
          <input
            matInput
            required
            autocomplete="off"
            placeholder="http://localhost:8080"
            data-field="baseUrl"
            [value]="form().baseUrl"
            (input)="set('baseUrl', $any($event.target).value)"
          />
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>API key (optional)</mat-label>
          <input
            matInput
            type="password"
            autocomplete="new-password"
            [value]="form().apiKey"
            (input)="set('apiKey', $any($event.target).value)"
          />
        </mat-form-field>

        <div class="llm-editor__models">
          <button
            mat-stroked-button
            type="button"
            data-action="get-models"
            [disabled]="loadingModels()"
            (click)="getModels()"
          >
            @if (loadingModels()) {
              <mat-progress-spinner mode="indeterminate" diameter="16" />
            } @else {
              <mat-icon>download</mat-icon>
            }
            Get models
          </button>
        </div>

        @if (modelChoices(); as choices) {
          <mat-form-field appearance="outline">
            <mat-label>Model (optional)</mat-label>
            <mat-select
              data-field="model"
              [value]="form().model"
              (valueChange)="set('model', $event)"
            >
              <mat-option value="">None</mat-option>
              @for (model of choices; track model) {
                <mat-option [value]="model">{{ model }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        } @else {
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Model (optional)</mat-label>
            <input
              matInput
              autocomplete="off"
              data-field="model"
              [value]="form().model"
              (input)="set('model', $any($event.target).value)"
            />
            @if (modelsHint()) {
              <mat-hint data-role="models-hint">{{ modelsHint() }}</mat-hint>
            }
          </mat-form-field>
        }

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Paragraphs per request</mat-label>
          <input
            matInput
            inputmode="numeric"
            data-field="attributionBatchSize"
            [value]="form().attributionBatchSize"
            (input)="set('attributionBatchSize', $any($event.target).value)"
          />
          <mat-hint>
            Character paragraphs attributed per LLM request. 1 = one at a time; higher values batch
            consecutive paragraphs into a single prompt.
          </mat-hint>
        </mat-form-field>

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Attribution prompt style</mat-label>
          <mat-select [value]="form().promptStyle" (valueChange)="set('promptStyle', $event)">
            @for (style of promptStyles; track style.value) {
              <mat-option [value]="style.value">{{ style.label }}</mat-option>
            }
          </mat-select>
          <mat-hint>
            Simple: only assigns a speaker when the text explicitly says who speaks ("said X");
            otherwise returns unknown so escalation passes the paragraph to a larger model. Full:
            uses inference heuristics.
          </mat-hint>
        </mat-form-field>

        <div>
          <mat-checkbox
            [checked]="form().supportsModelSwitch"
            (change)="set('supportsModelSwitch', $event.checked)"
          >
            Supports model switch
          </mat-checkbox>
          <p class="llm-editor__note">
            This endpoint (e.g. the local llama.cpp server) can load the configured model on demand.
          </p>
        </div>

        <mat-expansion-panel class="llm-editor__params" [expanded]="paramsOpen()">
          <mat-expansion-panel-header>
            <mat-panel-title>Request parameters (optional)</mat-panel-title>
          </mat-expansion-panel-header>
          <p class="llm-editor__note">Leave blank to use the server default.</p>
          <div class="llm-editor__param-grid">
            @for (field of numericFields; track field.key) {
              <mat-form-field appearance="outline">
                <mat-label>{{ field.label }}</mat-label>
                <input
                  matInput
                  inputmode="decimal"
                  [attr.data-field]="field.key"
                  [value]="form()[field.key]"
                  (input)="set(field.key, $any($event.target).value)"
                />
              </mat-form-field>
            }
          </div>
        </mat-expansion-panel>

        @if (error()) {
          <p class="llm-editor__error" role="alert">{{ error() }}</p>
        }
      </div>
    </r2m-config-editor-frame>
  `,
  styles: `
    :host {
      display: block;
    }
    .llm-editor {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      max-width: 640px;
    }
    .llm-editor__models mat-progress-spinner {
      display: inline-block;
      margin-right: var(--r2m-space-2);
      vertical-align: middle;
    }
    .llm-editor__note {
      margin: 0 0 var(--r2m-space-2);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .llm-editor__params {
      box-shadow: none;
      border: 1px solid var(--r2m-outline);
    }
    .llm-editor__param-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
      gap: 0 var(--r2m-space-3);
    }
    .llm-editor__error {
      margin: 0;
      color: var(--r2m-status-error);
    }
  `,
})
export class LlmConfigEditor {
  private readonly store = inject(LlmSettingsStore);
  private readonly api = inject(LlmSettingsApi);
  private readonly toast = inject(ToastService);

  readonly target = input.required<LlmEditTarget>();
  /** The stored config after a successful create or update. */
  readonly saved = output<LlmServerConfig>();

  protected readonly apiTypes = Object.values(LlmApiType).map((value) => ({
    value,
    label: API_TYPE_LABELS[value],
  }));
  protected readonly promptStyles = [
    { value: AttributionPromptStyle.Full, label: 'Full' },
    { value: AttributionPromptStyle.Simple, label: 'Simple' },
  ];
  protected readonly numericFields = NUMERIC_FIELDS;

  protected readonly form = signal<LlmConfigForm>(EMPTY_LLM_FORM);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly paramsOpen = signal(false);

  protected readonly models = signal<string[] | null>(null);
  protected readonly modelsHint = signal<string | null>(null);
  protected readonly loadingModels = signal(false);
  /** The fetched list plus the current model when the server did not list it; null = free text. */
  protected readonly modelChoices = computed(() => {
    const models = this.models();
    if (!models) return null;
    const current = this.form().model.trim();
    return current && !models.includes(current) ? [current, ...models] : models;
  });

  readonly dirty = computed(() => !sameLlmForm(this.form(), this.target().baseline));

  /** Which target the async answers belong to; one for an earlier target is dropped. */
  private generation = 0;

  constructor() {
    effect(() => {
      const target = this.target();
      untracked(() => this.open(target));
    });
  }

  private open(target: LlmEditTarget): void {
    this.generation++;
    this.form.set(target.draft);
    this.error.set(null);
    this.models.set(null);
    this.modelsHint.set(null);
    this.loadingModels.set(false);
    const b = target.draft;
    this.paramsOpen.set(
      !!(b.temperature || b.topP || b.maxTokens || b.frequencyPenalty || b.presencePenalty),
    );
  }

  protected set<K extends keyof LlmConfigForm>(key: K, value: LlmConfigForm[K]): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  /** Cancel: back to the saved state (an empty form for a new config). */
  protected reset(): void {
    this.form.set(this.target().baseline);
    this.error.set(null);
  }

  protected async getModels(): Promise<void> {
    const generation = this.generation;
    this.error.set(null);
    this.modelsHint.set(null);
    this.loadingModels.set(true);
    try {
      const models = await this.api.models(buildLlmConfig(this.form(), this.target().id));
      if (generation !== this.generation) return;
      this.models.set(models.length ? models : null);
      if (!models.length) this.modelsHint.set('Server returned no models — type one manually.');
    } catch (e) {
      if (generation !== this.generation) return;
      this.models.set(null);
      this.modelsHint.set(`Could not fetch models (${toApiError(e).message}) — type one manually.`);
    } finally {
      if (generation === this.generation) this.loadingModels.set(false);
    }
  }

  protected async save(): Promise<void> {
    const problem = validateLlmForm(this.form());
    this.error.set(problem);
    if (problem) return;

    const config = buildLlmConfig(this.form(), this.target().id);
    this.saving.set(true);
    try {
      if (config.id) {
        await this.store.update(config);
        this.toast.success('Configuration updated');
        this.saved.emit(config);
      } else {
        const created = await this.store.create(config);
        this.toast.success('Configuration created');
        this.saved.emit(created);
      }
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }
}
