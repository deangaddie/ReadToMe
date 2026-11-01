import { html, nothing } from 'lit-html';
import {
  type AttributionPromptStyle,
  type LlmApiType,
  type LlmServerConfig,
  LlmSettingsApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { configEditorFrame } from '@app/ui/config-editor-frame';
import { type FieldControlAttrs, field } from '@app/ui/field';
import { icon, spinner } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import {
  API_TYPE_LABELS,
  EMPTY_LLM_FORM,
  type LlmConfigForm,
  buildLlmConfig,
  sameLlmForm,
  validateLlmForm,
} from './llm-config-form';
import { LlmSettingsStore } from './llm-settings-store';
import '../shared/managed-service-status';

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

const API_TYPES = (Object.keys(API_TYPE_LABELS) as LlmApiType[]).map((value) => ({
  value,
  label: API_TYPE_LABELS[value],
}));
const PROMPT_STYLES = [
  { value: 'Full', label: 'Full' },
  { value: 'Simple', label: 'Simple' },
  { value: 'Chapter', label: 'Chapter' },
] as const satisfies readonly { value: AttributionPromptStyle; label: string }[];

/** The form fields typed as free text (everything but the API type, style and switch). */
type TextKey = {
  [K in keyof LlmConfigForm]: LlmConfigForm[K] extends string ? K : never;
}[keyof LlmConfigForm];

const valueOf = (e: Event) =>
  (e.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value;

/**
 * The in-place typed editor for one LLM config (ticket 21): the config's fields and validation
 * message inside the settings editor frame, plus the managed-container status for the draft's
 * base URL. Set `target`; `dirty()` tells the page whether edits would be lost; `saved` carries
 * the stored config after a successful create or update.
 */
export class LlmConfigEditor extends R2mElement {
  private readonly api = use(LlmSettingsApi);
  private readonly toast = use(ToastService);
  private store!: LlmSettingsStore;

  readonly #target = signal<LlmEditTarget | null>(null);
  get target() {
    return this.#target();
  }
  set target(value: LlmEditTarget | null) {
    this.#target.set(value);
  }

  readonly form = signal<LlmConfigForm>(EMPTY_LLM_FORM);
  readonly error = signal<string | null>(null);
  readonly saving = signal(false);
  readonly paramsOpen = signal(false);

  readonly models = signal<string[] | null>(null);
  readonly modelsHint = signal<string | null>(null);
  readonly loadingModels = signal(false);
  /** The fetched list plus the current model when the server did not list it; null = free text. */
  readonly modelChoices = computed(() => {
    const models = this.models();
    if (!models) return null;
    const current = this.form().model.trim();
    return current && !models.includes(current) ? [current, ...models] : models;
  });

  readonly dirty = computed(() => {
    const target = this.#target();
    return !!target && !sameLlmForm(this.form(), target.baseline);
  });

  /** Which target the async answers belong to; one for an earlier target is dropped. */
  private generation = 0;

  protected override connected(): void {
    this.classList.add('llm-editor-host');
    this.store = use(LlmSettingsStore, this);
    this.effect(() => {
      const target = this.#target();
      untracked(() => {
        if (target) this.open(target);
      });
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

  private set<K extends keyof LlmConfigForm>(key: K, value: LlmConfigForm[K]): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  /** Cancel: back to the saved state (an empty form for a new config). */
  private reset(): void {
    const target = this.#target();
    if (target) this.form.set(target.baseline);
    this.error.set(null);
  }

  protected template() {
    const target = this.#target();
    if (!target) return nothing;
    return configEditorFrame(
      {
        title: target.id ? target.baseline.name : 'New configuration',
        subtitle: target.id ? null : 'Saved configurations appear in the list.',
        dirty: this.dirty(),
        saving: this.saving(),
        onSave: () => void this.save(),
        onCancel: () => this.reset(),
      },
      this.body(),
    );
  }

  private body() {
    const form = this.form();
    const error = this.error();
    const choices = this.modelChoices();
    const hint = this.modelsHint();
    return html`<div class="llm-editor">
      <!-- Follows the draft, not the saved config: a duplicate or a retyped URL shows its container. -->
      <r2m-managed-service-status .baseUrl=${form.baseUrl}></r2m-managed-service-status>

      ${field({ id: 'llm-name', label: 'Name' }, (a) =>
        this.textInput(a, 'name', form.name, { required: true }),
      )}
      ${field({ id: 'llm-api-type', label: 'API type' }, (a) =>
        this.selectInput(a, form.apiType, API_TYPES, (v) => this.set('apiType', v as LlmApiType)),
      )}
      ${field({ id: 'llm-base-url', label: 'Base URL' }, (a) =>
        this.textInput(a, 'baseUrl', form.baseUrl, {
          required: true,
          placeholder: 'http://localhost:8080',
        }),
      )}
      ${field(
        { id: 'llm-api-key', label: 'API key (optional)' },
        (a) =>
          html`<input
            id=${a.id}
            aria-describedby=${a.describedBy}
            aria-invalid=${a.invalid}
            type="password"
            autocomplete="new-password"
            .value=${form.apiKey}
            @input=${(e: Event) => this.set('apiKey', valueOf(e))}
          />`,
      )}

      <div class="llm-editor__models">
        <button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-action="get-models"
          ?disabled=${this.loadingModels()}
          @click=${() => void this.getModels()}
        >
          ${this.loadingModels() ? spinner(16) : icon('download')} Get models
        </button>
      </div>

      ${
        choices
          ? field({ id: 'llm-model', label: 'Model (optional)' }, (a) =>
              this.selectInput(
                a,
                form.model,
                [{ value: '', label: 'None' }, ...choices.map((m) => ({ value: m, label: m }))],
                (v) => this.set('model', v),
                'model',
              ),
            )
          : field(
              {
                id: 'llm-model',
                label: 'Model (optional)',
                hint: hint ? html`<span data-role="models-hint">${hint}</span>` : null,
              },
              (a) => this.textInput(a, 'model', form.model),
            )
      }
      ${field(
        {
          id: 'llm-batch-size',
          label: 'Paragraphs per request',
          hint: 'Character paragraphs attributed per LLM request. 1 = one at a time; higher values batch consecutive paragraphs into a single prompt.',
        },
        (a) =>
          this.textInput(a, 'attributionBatchSize', form.attributionBatchSize, {
            inputmode: 'numeric',
          }),
      )}
      ${field(
        {
          id: 'llm-prompt-style',
          label: 'Attribution prompt style',
          hint: 'Simple: only assigns a speaker when the text explicitly says who speaks ("said X"); otherwise returns unknown so escalation passes the paragraph to a larger model. Full: uses inference heuristics. Chapter: whole chapter, roster-only answers, rules pre-tag and rules-discover. Fast on gemma-12b; the model never creates characters — rules-discover does. Thinking is always off.',
        },
        (a) =>
          this.selectInput(a, form.promptStyle, PROMPT_STYLES, (v) =>
            this.set('promptStyle', v as AttributionPromptStyle),
          ),
      )}

      <div>
        <label class="r2m-checkbox">
          <input
            type="checkbox"
            .checked=${form.supportsModelSwitch}
            @change=${(e: Event) =>
              this.set('supportsModelSwitch', (e.target as HTMLInputElement).checked)}
          />
          Supports model switch
        </label>
        <p class="llm-editor__note">
          This endpoint (e.g. the local llama.cpp server) can load the configured model on demand.
        </p>
      </div>

      <details
        class="r2m-expansion llm-editor__params"
        ?open=${this.paramsOpen()}
        @toggle=${(e: Event) => this.paramsOpen.set((e.target as HTMLDetailsElement).open)}
      >
        <summary><span class="r2m-expansion__title">Request parameters (optional)</span></summary>
        <div class="r2m-expansion__body">
          <p class="llm-editor__note">Leave blank to use the server default.</p>
          <div class="llm-editor__param-grid">
            ${NUMERIC_FIELDS.map((f) =>
              field({ id: `llm-${f.key}`, label: f.label }, (a) =>
                this.textInput(a, f.key, form[f.key], { inputmode: 'decimal' }),
              ),
            )}
          </div>
        </div>
      </details>

      ${error ? html`<p class="llm-editor__error" role="alert">${error}</p>` : nothing}
    </div>`;
  }

  private textInput(
    a: FieldControlAttrs,
    key: TextKey,
    value: string,
    o: { required?: boolean; placeholder?: string; inputmode?: string } = {},
  ) {
    return html`<input
      id=${a.id}
      aria-describedby=${a.describedBy}
      aria-invalid=${a.invalid}
      type="text"
      autocomplete="off"
      ?required=${o.required ?? false}
      placeholder=${o.placeholder ?? nothing}
      inputmode=${o.inputmode ?? nothing}
      data-field=${key}
      .value=${value}
      @input=${(e: Event) => this.set(key, valueOf(e))}
    />`;
  }

  /** A native `<select>`; `?selected` too, so the right option shows before `.value` lands. */
  private selectInput(
    a: FieldControlAttrs,
    value: string,
    options: readonly { value: string | number; label: string }[],
    onChange: (value: string) => void,
    dataField?: string,
  ) {
    return html`<select
      id=${a.id}
      aria-describedby=${a.describedBy}
      aria-invalid=${a.invalid}
      data-field=${dataField ?? nothing}
      .value=${value}
      @change=${(e: Event) => onChange(valueOf(e))}
    >
      ${options.map(
        (o) =>
          html`<option value=${o.value} ?selected=${String(o.value) === value}>${o.label}</option>`,
      )}
    </select>`;
  }

  private async getModels(): Promise<void> {
    const target = this.#target();
    if (!target) return;
    const generation = this.generation;
    this.error.set(null);
    this.modelsHint.set(null);
    this.loadingModels.set(true);
    try {
      const models = await this.api.models(buildLlmConfig(this.form(), target.id));
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

  private async save(): Promise<void> {
    const target = this.#target();
    if (!target) return;
    const problem = validateLlmForm(this.form());
    this.error.set(problem);
    if (problem) return;

    const config = buildLlmConfig(this.form(), target.id);
    this.saving.set(true);
    try {
      if (config.id) {
        await this.store.update(config);
        this.toast.success('Configuration updated');
        this.emit<LlmServerConfig>('saved', config);
      } else {
        const created = await this.store.create(config);
        this.toast.success('Configuration created');
        this.emit<LlmServerConfig>('saved', created);
      }
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }
}
define('r2m-llm-config-editor', LlmConfigEditor);
