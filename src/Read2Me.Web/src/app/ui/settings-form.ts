import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { computed, signal, untracked } from '@app/core/signals';
import { icon } from './partials';

export type SettingsFieldKind = 'number' | 'boolean' | 'enum' | 'string' | 'text';

export interface SettingsField {
  key: string;
  label: string;
  kind: SettingsFieldKind;
  min?: number;
  max?: number;
  step?: number;
  options?: { value: string; label?: string }[];
  default: unknown;
  help?: string;
  /** A number that may be left blank (null), meaning "use the server's default". */
  nullable?: boolean;
  /** A string that is a credential: typed masked and kept out of the browser's autofill. */
  secret?: boolean;
}

export interface SettingsSchema {
  type: string;
  fields: SettingsField[];
}

export type SettingsValues = Record<string, unknown>;
export type SettingsFormMode = 'defaults' | 'override';

/** Effective value for a field: the explicit value when present, else the schema default. */
export function effectiveValue(field: SettingsField, values: SettingsValues): unknown {
  return Object.prototype.hasOwnProperty.call(values, field.key)
    ? values[field.key]
    : field.default;
}

/** Keys whose value differs from the schema default — the sparse patch of override mode. */
export function sparsePatch(schema: SettingsSchema, values: SettingsValues): SettingsValues {
  const patch: SettingsValues = {};
  for (const field of schema.fields) {
    if (!Object.prototype.hasOwnProperty.call(values, field.key)) continue;
    if (values[field.key] !== field.default) patch[field.key] = values[field.key];
  }
  return patch;
}

export function isFieldValid(field: SettingsField, value: unknown): boolean {
  if (field.kind !== 'number') return true;
  if (value == null) return !!field.nullable;
  if (typeof value !== 'number' || Number.isNaN(value)) return false;
  if (field.min !== undefined && value < field.min) return false;
  if (field.max !== undefined && value > field.max) return false;
  return true;
}

const EMPTY_SCHEMA: SettingsSchema = { type: '', fields: [] };

const valueOf = (event: Event): string =>
  (event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;

/** A field value as the control's text: primitives as they print, anything else as JSON. */
function asText(value: unknown): string {
  if (value == null) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return JSON.stringify(value);
}

/**
 * Schema-driven settings form (design §7): renders number / boolean / enum / string / text fields
 * on native controls — a range + number pair for a bounded number, a `role=switch` checkbox, a
 * `<select>`, an input or a textarea. `defaults` mode edits the full value set; `override` mode
 * edits a sparse patch over the schema defaults with an "overridden" dot and a per-field reset.
 * Emits `values-change` with the values (full set or sparse patch) and `valid-change` when the
 * form's validity flips.
 */
export class SettingsForm extends R2mElement {
  readonly #schema = signal<SettingsSchema>(EMPTY_SCHEMA);
  get schema() {
    return this.#schema();
  }
  set schema(value: SettingsSchema) {
    this.#schema.set(value);
  }

  readonly #mode = signal<SettingsFormMode>('defaults');
  get mode() {
    return this.#mode();
  }
  set mode(value: SettingsFormMode) {
    this.#mode.set(value);
  }

  readonly #values = signal<SettingsValues>({});
  get values() {
    return this.#values();
  }
  set values(value: SettingsValues) {
    this.#values.set(value);
  }

  readonly #disabled = signal(false);
  get disabled() {
    return this.#disabled();
  }
  set disabled(value: boolean) {
    this.#disabled.set(value);
  }

  /** Local working copy; re-seeded whenever the host hands in new values. */
  readonly #working = signal<SettingsValues>({});

  readonly valid = computed(() =>
    this.#schema().fields.every((f) => isFieldValid(f, effectiveValue(f, this.#working()))),
  );

  protected override connected(): void {
    this.classList.add('r2m-settings-form');
    this.effect(() => {
      const incoming = this.#values();
      untracked(() => this.#working.set({ ...incoming }));
    });
    let last: boolean | undefined;
    this.effect(() => {
      const v = this.valid();
      if (v !== last) {
        last = v;
        this.emit('valid-change', v);
      }
    });
  }

  protected template() {
    const override = this.#mode() === 'override';
    this.classList.toggle('r2m-settings-form--override', override);
    const disabled = this.#disabled();
    // Read eagerly, not inside a lazy directive, so the element's effect tracks every signal.
    return this.#schema().fields.map((field) => {
      const overridden = this.isOverridden(field);
      const valid = this.fieldValid(field);
      return html`<div
        class="r2m-settings-form__field ${overridden ? 'r2m-settings-form__field--overridden' : ''} ${
          valid ? '' : 'r2m-settings-form__field--invalid'
        }"
        data-key=${field.key}
      >
        <div class="r2m-settings-form__label-row">
          ${
            override
              ? html`<span
                  class="r2m-settings-form__dot ${overridden ? 'r2m-settings-form__dot--on' : ''}"
                  title=${overridden ? 'Overridden' : 'Using provider default'}
                  aria-hidden="true"
                ></span>`
              : nothing
          }
          <span class="r2m-settings-form__label">${field.label}</span>
          ${
            override && overridden
              ? html`<button
                  type="button"
                  class="r2m-icon-button r2m-settings-form__reset"
                  ?disabled=${disabled}
                  @click=${() => this.reset(field)}
                  aria-label=${`Reset ${field.label} to default`}
                  data-tooltip="Reset to default"
                >
                  ${icon('restart_alt')}
                </button>`
              : nothing
          }
        </div>
        ${this.control(field, disabled)}
        ${
          valid
            ? field.help
              ? html`<p class="r2m-settings-form__help">${field.help}</p>`
              : nothing
            : html`<p class="r2m-settings-form__error" role="alert">
                Must be a
                number${field.min !== undefined ? ` ≥ ${field.min}` : ''}${
                  field.max !== undefined ? ` ≤ ${field.max}` : ''
                }${field.nullable ? ', or blank' : ''}.
              </p>`
        }
      </div>`;
    });
  }

  private control(field: SettingsField, disabled: boolean) {
    switch (field.kind) {
      case 'number': {
        const number = html`<input
          class="r2m-settings-form__number"
          type="number"
          aria-label=${field.label}
          min=${field.min ?? nothing}
          max=${field.max ?? nothing}
          step=${field.step ?? 'any'}
          .value=${String(this.numberValue(field))}
          ?disabled=${disabled}
          @input=${(e: Event) => this.setValue(field, this.parseNumber(field, valueOf(e)))}
        />`;
        if (field.min !== undefined && field.max !== undefined) {
          return html`<div class="r2m-settings-form__slider-row">
            <input
              class="r2m-range"
              type="range"
              aria-label=${`${field.label} slider`}
              min=${field.min}
              max=${field.max}
              step=${field.step ?? 1}
              .value=${String(this.sliderValue(field))}
              ?disabled=${disabled}
              @input=${(e: Event) => this.setValue(field, Number(valueOf(e)))}
            />
            ${number}
          </div>`;
        }
        return number;
      }
      case 'boolean':
        return html`<label class="r2m-switch r2m-settings-form__switch">
          <input
            type="checkbox"
            role="switch"
            aria-label=${field.label}
            .checked=${!!this.value(field)}
            ?disabled=${disabled}
            @change=${(e: Event) => this.setValue(field, (e.target as HTMLInputElement).checked)}
          />
        </label>`;
      case 'enum': {
        const current = this.value(field);
        return html`<select
          class="r2m-settings-form__select"
          aria-label=${field.label}
          .value=${asText(current)}
          ?disabled=${disabled}
          @change=${(e: Event) => this.setValue(field, valueOf(e))}
        >
          ${(field.options ?? []).map(
            (opt) =>
              html`<option value=${opt.value} ?selected=${opt.value === current}>
                ${opt.label ?? opt.value}
              </option>`,
          )}
        </select>`;
      }
      case 'text':
        return html`<textarea
          class="r2m-settings-form__text"
          aria-label=${field.label}
          .value=${this.stringValue(field)}
          ?disabled=${disabled}
          @input=${(e: Event) => this.setValue(field, valueOf(e))}
        ></textarea>`;
      default:
        return html`<input
          class="r2m-settings-form__text"
          type=${field.secret ? 'password' : 'text'}
          autocomplete=${field.secret ? 'new-password' : 'off'}
          aria-label=${field.label}
          .value=${this.stringValue(field)}
          ?disabled=${disabled}
          @input=${(e: Event) => this.setValue(field, valueOf(e))}
        />`;
    }
  }

  private value(field: SettingsField): unknown {
    return effectiveValue(field, this.#working());
  }

  /** Blank for a nullable field left at null; the slider thumb falls back to the range start. */
  private numberValue(field: SettingsField): number | string {
    const v = this.value(field);
    if (v == null) return field.nullable ? '' : 0;
    if (typeof v === 'number') return Number.isNaN(v) ? '' : v;
    return Number(v);
  }

  private sliderValue(field: SettingsField): number {
    const v = this.numberValue(field);
    return typeof v === 'number' && !Number.isNaN(v) ? v : (field.min ?? 0);
  }

  private stringValue(field: SettingsField): string {
    return asText(this.value(field));
  }

  private isOverridden(field: SettingsField): boolean {
    return Object.prototype.hasOwnProperty.call(this.#working(), field.key);
  }

  private fieldValid(field: SettingsField): boolean {
    return isFieldValid(field, this.value(field));
  }

  /** Blank means null on a nullable field and "not a number" (invalid) on any other. */
  private parseNumber(field: SettingsField, raw: string): number | null {
    if (raw.trim() === '') return field.nullable ? null : Number.NaN;
    return Number(raw);
  }

  private setValue(field: SettingsField, value: unknown): void {
    this.#working.update((w) => ({ ...w, [field.key]: value }));
    this.emitValues();
  }

  private reset(field: SettingsField): void {
    this.#working.update((w) => {
      const next = { ...w };
      delete next[field.key];
      return next;
    });
    this.emitValues();
  }

  private emitValues(): void {
    const schema = this.#schema();
    const w = this.#working();
    if (this.#mode() === 'override') {
      this.emit<SettingsValues>('values-change', sparsePatch(schema, w));
      return;
    }
    const full: SettingsValues = {};
    for (const field of schema.fields) full[field.key] = effectiveValue(field, w);
    this.emit<SettingsValues>('values-change', full);
  }
}
define('r2m-settings-form', SettingsForm);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-settings-form': SettingsForm;
  }
}
