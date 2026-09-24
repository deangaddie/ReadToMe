import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ParagraphTtsSettingsApi, TextStep, toApiError } from '@app/api';
import { toFormField } from '@app/shared/settings-schema';
import { ConfigEditorFrame } from '@app/ui/config-editor-frame/config-editor-frame';
import {
  SettingsField,
  SettingsForm,
  SettingsSchema,
  SettingsValues,
} from '@app/ui/settings-form/settings-form';
import { ToastService } from '@app/ui/toast/toast.service';
import { ManagedServiceStatus } from '../shared/managed-service-status';
import { ProviderArea, providerType } from './provider-area';
import {
  ProviderConfig,
  ProviderForm,
  buildProviderConfig,
  emptyProviderForm,
  fieldValues,
  mergeFieldValues,
  sameProviderForm,
  validateProviderForm,
} from './provider-form';
import { ProviderSettingsStore } from './provider-settings-store';
import { TextProcessingEdit, TextProcessingEditor } from './text-processing-editor';

/** What the editor is editing: a saved config (`id` > 0) or a new one, which may start pre-filled. */
export interface ProviderEditTarget {
  id: number;
  /** The saved state; Cancel returns here and dirty is measured against it. */
  baseline: ProviderForm;
  /** The opening draft — differs from `baseline` for a duplicate. */
  draft: ProviderForm;
}

/**
 * The in-place typed editor for one provider config of any area (ticket 22): name, type and base
 * URL, then the area's own per-config fields and the provider type's tuning fields from the host's
 * schema in one `r2m-settings-form`, then — for TTS — text processing. Blazor's config dialogs,
 * inside the settings editor frame, plus the managed-container status for the draft base URL.
 */
@Component({
  selector: 'app-provider-config-editor',
  imports: [
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    ConfigEditorFrame,
    SettingsForm,
    ManagedServiceStatus,
    TextProcessingEditor,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-config-editor-frame
      [title]="target().id ? target().baseline.name : 'New configuration'"
      [subtitle]="target().id ? undefined : 'Saved configurations appear in the list.'"
      [dirty]="dirty()"
      [saving]="saving()"
      [canSave]="fields() !== null"
      (save)="save()"
      (cancel)="reset()"
    >
      <div class="provider-editor">
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
          <mat-label>Type</mat-label>
          <mat-select data-field="type" [value]="form().type" (valueChange)="setType($event)">
            @for (type of area().types; track type.value) {
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
            data-field="baseUrl"
            [placeholder]="type().urlExample"
            [value]="form().baseUrl"
            (input)="set('baseUrl', $any($event.target).value)"
          />
        </mat-form-field>

        @if (schema(); as schema) {
          @if (schema.fields.length) {
            <r2m-settings-form
              class="provider-editor__settings"
              [schema]="schema"
              [values]="values()"
              (valuesChange)="setValues($event)"
            />
          }
        } @else if (!schemaError()) {
          <mat-progress-bar mode="indeterminate" />
        }

        @if (form().text; as text) {
          <app-text-processing-editor
            [text]="text"
            [steps]="steps()"
            (textEdit)="editText($event)"
          />
        }

        @if (schemaError() ?? error(); as problem) {
          <p class="provider-editor__error" role="alert">{{ problem }}</p>
        }
      </div>
    </r2m-config-editor-frame>
  `,
  styles: `
    :host {
      display: block;
    }
    .provider-editor {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      max-width: 640px;
    }
    .provider-editor__settings {
      margin-bottom: var(--r2m-space-4);
    }
    .provider-editor__error {
      margin: 0;
      color: var(--r2m-status-error);
    }
  `,
})
export class ProviderConfigEditor {
  private readonly store = inject(ProviderSettingsStore);
  private readonly injector = inject(Injector);
  private readonly toast = inject(ToastService);

  readonly area = input.required<ProviderArea>();
  readonly target = input.required<ProviderEditTarget>();
  /** The stored config after a successful create or update. */
  readonly saved = output<ProviderConfig>();

  protected readonly form = signal<ProviderForm>(emptyProviderForm(0, false));
  protected readonly error = signal<string | null>(null);
  protected readonly schemaError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly steps = signal<TextStep[]>([]);

  protected readonly type = computed(() => providerType(this.area(), this.form().type));

  /** The provider type's tuning fields, per type; a type is asked for once. */
  private readonly schemas = signal<ReadonlyMap<number, SettingsField[]>>(new Map());

  /** Every field below the base URL; null while the type's schema is on its way. */
  protected readonly fields = computed<SettingsField[] | null>(() => {
    const tuning = this.schemas().get(this.form().type);
    if (!tuning) return null;
    return [...this.area().configFields(this.form().type, this.form().settings), ...tuning];
  });
  protected readonly schema = computed<SettingsSchema | null>(() => {
    const fields = this.fields();
    return fields && { type: this.type().label, fields };
  });
  protected readonly values = computed(() => fieldValues(this.fields() ?? [], this.form().settings));

  readonly dirty = computed(() => !sameProviderForm(this.form(), this.target().baseline));

  /** Which target the async answers belong to; one for an earlier target is dropped. */
  private generation = 0;

  constructor() {
    effect(() => {
      const target = this.target();
      untracked(() => this.open(target));
    });
    effect(() => {
      const type = this.form().type;
      untracked(() => void this.loadSchema(type));
    });
  }

  private open(target: ProviderEditTarget): void {
    this.generation++;
    this.form.set(target.draft);
    this.error.set(null);
    if (this.area().hasTextProcessing) void this.loadSteps(target.id);
  }

  private async loadSchema(type: number): Promise<void> {
    // Cleared first: a failure belongs to the type that was showing, not to this one.
    this.schemaError.set(null);
    if (this.schemas().has(type)) return;
    try {
      const schema = await this.injector.get(this.area().api).schema(type);
      const fields = schema.fields.map((f) => toFormField(f));
      this.schemas.update((known) => new Map(known).set(type, fields));
    } catch (e) {
      if (this.form().type === type)
        this.schemaError.set(`The provider's settings could not be loaded: ${toApiError(e).message}`);
    }
  }

  private async loadSteps(configId: number): Promise<void> {
    const generation = this.generation;
    try {
      const steps = await this.injector.get(ParagraphTtsSettingsApi).textSteps(configId);
      if (generation === this.generation) this.steps.set(steps);
    } catch (e) {
      if (generation === this.generation)
        this.error.set(`The text processing steps could not be loaded: ${toApiError(e).message}`);
    }
  }

  protected set<K extends keyof ProviderForm>(key: K, value: ProviderForm[K]): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  protected editText(edit: TextProcessingEdit): void {
    this.form.update((f) => (f.text ? { ...f, text: edit(f.text) } : f));
  }

  /**
   * Another provider type is another settings record: its tuning starts from that type's defaults.
   * What the area holds per config (chunking, carrier prefix) stays, as it does in Blazor's dialog.
   */
  protected setType(type: number): void {
    const kept = this.area().perConfigKeys;
    this.form.update((f) =>
      f.type === type
        ? f
        : {
            ...f,
            type,
            settings: Object.fromEntries(
              Object.entries(f.settings).filter(([key]) => kept.includes(key)),
            ),
          },
    );
  }

  protected setValues(values: SettingsValues): void {
    this.form.update((f) => ({ ...f, settings: mergeFieldValues(f.settings, values) }));
  }

  /** Cancel: back to the saved state (an empty form for a new config). */
  protected reset(): void {
    this.form.set(this.target().baseline);
    this.error.set(null);
  }

  protected async save(): Promise<void> {
    const fields = this.fields();
    if (!fields) return;
    const form = this.form();
    const problem =
      validateProviderForm(form, fields, this.type().urlExample) ??
      this.area().validate?.(form) ??
      null;
    this.error.set(problem);
    if (problem) return;

    const config = buildProviderConfig(form, this.target().id, fields);
    this.saving.set(true);
    try {
      const stored = config.id ? await this.store.update(config) : await this.store.create(config);
      this.toast.success(config.id ? 'Configuration updated' : 'Configuration created');
      this.saved.emit(stored);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }
}
