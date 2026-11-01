import { html, nothing } from 'lit-html';
import { ParagraphTtsSettingsApi, type ProviderType, type TextStepDto, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { toFormField } from '@app/shared/settings-schema';
import { configEditorFrame } from '@app/ui/config-editor-frame';
import { type FieldControlAttrs, controlValue, field } from '@app/ui/field';
import type { SettingsField, SettingsSchema, SettingsValues } from '@app/ui/settings-form';
import { ToastService } from '@app/ui/toast';
import { type ProviderArea, providerType } from './provider-area';
import {
  type ProviderConfig,
  type ProviderForm,
  buildProviderConfig,
  emptyProviderForm,
  fieldValues,
  mergeFieldValues,
  sameProviderForm,
  validateProviderForm,
} from './provider-form';
import { ProviderSettingsStore } from './provider-settings-store';
import { type TextProcessingEdit, textProcessingEditor } from './text-processing-editor';
import '@app/ui/settings-form';
import '../shared/managed-service-status';

/** What the editor is editing: a saved config (`id` > 0) or a new one, which may start pre-filled. */
export interface ProviderEditTarget {
  id: number;
  /** The saved state; Cancel returns here and dirty is measured against it. */
  baseline: ProviderForm;
  /** The opening draft — differs from `baseline` for a duplicate. */
  draft: ProviderForm;
}

/**
 * The in-place typed editor for one provider config of any area (native-web 35): name, type and base
 * URL, then the area's own per-config fields and the provider type's tuning fields from the host's
 * schema in one `r2m-settings-form`, then — for TTS — text processing, inside the settings editor
 * frame, plus the managed-container status for the draft base URL. Set `area` and `target`;
 * `dirty()` tells the page whether edits would be lost; `saved` carries the stored config after
 * a successful create or update.
 */
export class ProviderConfigEditor extends R2mElement {
  private readonly ttsApi = use(ParagraphTtsSettingsApi);
  private readonly toast = use(ToastService);
  private store!: ProviderSettingsStore;

  readonly #area = signal<ProviderArea | null>(null);
  get area() {
    return this.#area();
  }
  set area(value: ProviderArea | null) {
    this.#area.set(value);
  }

  readonly #target = signal<ProviderEditTarget | null>(null);
  get target() {
    return this.#target();
  }
  set target(value: ProviderEditTarget | null) {
    this.#target.set(value);
  }

  // A placeholder until a target arrives; opening one resets the form to its type.
  readonly form = signal<ProviderForm>(emptyProviderForm('VoxCpm2', false));
  readonly error = signal<string | null>(null);
  readonly schemaError = signal<string | null>(null);
  readonly saving = signal(false);
  readonly steps = signal<TextStepDto[]>([]);

  readonly type = computed(() => {
    const area = this.#area();
    return area ? providerType(area, this.form().type) : null;
  });

  /** The provider type's tuning fields, per type; a type is asked for once. */
  private readonly schemas = signal<ReadonlyMap<ProviderType, SettingsField[]>>(new Map());

  /** Every field below the base URL; null while the type's schema is on its way. */
  readonly fields = computed<SettingsField[] | null>(() => {
    const area = this.#area();
    const tuning = this.schemas().get(this.form().type);
    if (!area || !tuning) return null;
    return [...area.configFields(this.form().type, this.form().settings), ...tuning];
  });
  readonly schema = computed<SettingsSchema | null>(() => {
    const fields = this.fields();
    const type = this.type();
    return fields && type ? { type: type.label, fields } : null;
  });
  readonly values = computed(() => fieldValues(this.fields() ?? [], this.form().settings));

  readonly dirty = computed(() => {
    const target = this.#target();
    return !!target && !sameProviderForm(this.form(), target.baseline);
  });

  /** Which target the async answers belong to; one for an earlier target is dropped. */
  private generation = 0;

  protected override connected(): void {
    this.classList.add('provider-editor-host');
    this.store = use(ProviderSettingsStore, this);
    this.effect(() => {
      const target = this.#target();
      untracked(() => {
        if (target) this.open(target);
      });
    });
    this.effect(() => {
      const type = this.form().type;
      const area = this.#area();
      untracked(() => {
        if (area) void this.loadSchema(area, type);
      });
    });
  }

  private open(target: ProviderEditTarget): void {
    this.generation++;
    this.form.set(target.draft);
    this.error.set(null);
    if (this.#area()?.hasTextProcessing) void this.loadSteps(target.id);
  }

  private async loadSchema(area: ProviderArea, type: ProviderType): Promise<void> {
    // Cleared first: a failure belongs to the type that was showing, not to this one.
    this.schemaError.set(null);
    if (this.schemas().has(type)) return;
    try {
      const schema = await this.store.schema(type);
      const fields = schema.fields.map((f) => toFormField(f));
      this.schemas.update((known) => new Map(known).set(type, fields));
    } catch (e) {
      if (this.form().type === type && this.#area() === area)
        this.schemaError.set(
          `The provider's settings could not be loaded: ${toApiError(e).message}`,
        );
    }
  }

  private async loadSteps(configId: number): Promise<void> {
    const generation = this.generation;
    try {
      const steps = await this.ttsApi.textSteps(configId);
      if (generation === this.generation) this.steps.set(steps);
    } catch (e) {
      if (generation === this.generation)
        this.error.set(`The text processing steps could not be loaded: ${toApiError(e).message}`);
    }
  }

  private set<K extends keyof ProviderForm>(key: K, value: ProviderForm[K]): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  private editText(edit: TextProcessingEdit): void {
    this.form.update((f) => (f.text ? { ...f, text: edit(f.text) } : f));
  }

  /**
   * Another provider type is another settings record: its tuning starts from that type's defaults.
   * What the area holds per config (chunking, carrier prefix) stays.
   */
  setType(type: ProviderType): void {
    const kept = this.#area()?.perConfigKeys ?? [];
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

  private setValues(values: SettingsValues): void {
    this.form.update((f) => ({ ...f, settings: mergeFieldValues(f.settings, values) }));
  }

  /** Cancel: back to the saved state (an empty form for a new config). */
  private reset(): void {
    const target = this.#target();
    if (target) this.form.set(target.baseline);
    this.error.set(null);
  }

  protected template() {
    const target = this.#target();
    const area = this.#area();
    if (!target || !area) return nothing;
    return configEditorFrame(
      {
        title: target.id ? target.baseline.name : 'New configuration',
        subtitle: target.id ? null : 'Saved configurations appear in the list.',
        dirty: this.dirty(),
        saving: this.saving(),
        canSave: this.fields() !== null,
        onSave: () => void this.save(),
        onCancel: () => this.reset(),
      },
      this.body(area),
    );
  }

  private body(area: ProviderArea) {
    const form = this.form();
    const schema = this.schema();
    const schemaError = this.schemaError();
    const problem = schemaError ?? this.error();
    const type = this.type();
    return html`<div class="provider-editor">
      <!-- Follows the draft, not the saved config: a duplicate or a retyped URL shows its container. -->
      <r2m-managed-service-status .baseUrl=${form.baseUrl}></r2m-managed-service-status>

      ${field({ id: 'provider-name', label: 'Name' }, (a) =>
        this.textInput(a, 'name', form.name, { required: true }),
      )}
      ${field(
        { id: 'provider-type', label: 'Type' },
        (a) =>
          html`<select
            id=${a.id}
            aria-describedby=${a.describedBy}
            aria-invalid=${a.invalid}
            data-field="type"
            .value=${form.type}
            @change=${(e: Event) => this.setType(controlValue(e) as ProviderType)}
          >
            ${area.types.map(
              (t) =>
                html`<option value=${t.value} ?selected=${t.value === form.type}>
                  ${t.label}
                </option>`,
            )}
          </select>`,
      )}
      ${field({ id: 'provider-base-url', label: 'Base URL' }, (a) =>
        this.textInput(a, 'baseUrl', form.baseUrl, {
          required: true,
          placeholder: type?.urlExample ?? '',
        }),
      )}
      ${
        schema
          ? schema.fields.length
            ? html`<r2m-settings-form
                class="provider-editor__settings"
                .schema=${schema}
                .values=${this.values()}
                @values-change=${(e: CustomEvent<SettingsValues>) => this.setValues(e.detail)}
              ></r2m-settings-form>`
            : nothing
          : schemaError
            ? nothing
            : html`<progress
                class="provider-editor__progress"
                aria-label="Loading settings"
              ></progress>`
      }
      ${
        form.text
          ? textProcessingEditor({
              text: form.text,
              steps: this.steps(),
              onEdit: (edit) => this.editText(edit),
            })
          : nothing
      }
      ${problem ? html`<p class="provider-editor__error" role="alert">${problem}</p>` : nothing}
    </div>`;
  }

  private textInput(
    a: FieldControlAttrs,
    key: 'name' | 'baseUrl',
    value: string,
    o: { required?: boolean; placeholder?: string } = {},
  ) {
    return html`<input
      id=${a.id}
      aria-describedby=${a.describedBy}
      aria-invalid=${a.invalid}
      type="text"
      autocomplete="off"
      ?required=${o.required ?? false}
      placeholder=${o.placeholder ?? nothing}
      data-field=${key}
      .value=${value}
      @input=${(e: Event) => this.set(key, controlValue(e))}
    />`;
  }

  private async save(): Promise<void> {
    const target = this.#target();
    const area = this.#area();
    const fields = this.fields();
    const type = this.type();
    if (!target || !area || !fields || !type) return;
    const form = this.form();
    const problem =
      validateProviderForm(form, fields, type.urlExample) ?? area.validate?.(form) ?? null;
    this.error.set(problem);
    if (problem) return;

    const config = buildProviderConfig(form, target.id, fields);
    this.saving.set(true);
    try {
      const stored = config.id ? await this.store.update(config) : await this.store.create(config);
      this.toast.success(config.id ? 'Configuration updated' : 'Configuration created');
      this.emit<ProviderConfig>('saved', stored);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }
}
define('r2m-provider-config-editor', ProviderConfigEditor);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-provider-config-editor': ProviderConfigEditor;
  }
}
