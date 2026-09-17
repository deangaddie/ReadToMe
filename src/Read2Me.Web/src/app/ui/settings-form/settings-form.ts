import { TextFieldModule } from '@angular/cdk/text-field';
import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  effect,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';

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

/**
 * Schema-driven settings form (design §7): renders number / boolean / enum / string / text fields.
 * `defaults` mode edits the full value set; `override` mode edits a sparse patch over the schema
 * defaults with an "overridden" dot and a per-field reset.
 */
@Component({
  selector: 'r2m-settings-form',
  imports: [
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatSliderModule,
    MatIconModule,
    MatButtonModule,
    MatTooltipModule,
    TextFieldModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-settings-form',
    '[class.r2m-settings-form--override]': 'mode() === "override"',
  },
  template: `
    @for (field of schema().fields; track field.key) {
      <div
        class="r2m-settings-form__field"
        [class.r2m-settings-form__field--overridden]="isOverridden(field)"
        [class.r2m-settings-form__field--invalid]="!fieldValid(field)"
        [attr.data-key]="field.key"
      >
        <div class="r2m-settings-form__label-row">
          @if (mode() === 'override') {
            <span
              class="r2m-settings-form__dot"
              [class.r2m-settings-form__dot--on]="isOverridden(field)"
              [attr.title]="isOverridden(field) ? 'Overridden' : 'Using provider default'"
              aria-hidden="true"
            ></span>
          }
          <span class="r2m-settings-form__label">{{ field.label }}</span>
          @if (mode() === 'override' && isOverridden(field)) {
            <button
              mat-icon-button
              type="button"
              class="r2m-settings-form__reset"
              [disabled]="disabled()"
              (click)="reset(field)"
              [attr.aria-label]="'Reset ' + field.label + ' to default'"
              matTooltip="Reset to default"
            >
              <mat-icon>restart_alt</mat-icon>
            </button>
          }
        </div>

        @switch (field.kind) {
          @case ('number') {
            @if (field.min !== undefined && field.max !== undefined) {
              <div class="r2m-settings-form__slider-row">
                <mat-slider
                  [min]="field.min"
                  [max]="field.max"
                  [step]="field.step ?? 1"
                  [disabled]="disabled()"
                  discrete
                >
                  <input
                    matSliderThumb
                    [value]="sliderValue(field)"
                    (valueChange)="setValue(field, $event)"
                  />
                </mat-slider>
                <input
                  class="r2m-settings-form__number"
                  type="number"
                  [attr.aria-label]="field.label"
                  [min]="field.min"
                  [max]="field.max"
                  [step]="field.step ?? 'any'"
                  [value]="numberValue(field)"
                  [disabled]="disabled()"
                  (input)="setValue(field, parseNumber(field, $any($event.target).value))"
                />
              </div>
            } @else {
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <input
                  matInput
                  type="number"
                  [attr.aria-label]="field.label"
                  [step]="field.step ?? 'any'"
                  [value]="numberValue(field)"
                  [disabled]="disabled()"
                  (input)="setValue(field, parseNumber(field, $any($event.target).value))"
                />
              </mat-form-field>
            }
          }
          @case ('boolean') {
            <mat-slide-toggle
              [checked]="!!value(field)"
              [disabled]="disabled()"
              [attr.aria-label]="field.label"
              (change)="setValue(field, $event.checked)"
            />
          }
          @case ('enum') {
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-select
                [value]="value(field)"
                [disabled]="disabled()"
                [attr.aria-label]="field.label"
                (selectionChange)="setValue(field, $event.value)"
              >
                @for (opt of field.options ?? []; track opt.value) {
                  <mat-option [value]="opt.value">{{ opt.label ?? opt.value }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          }
          @case ('text') {
            <mat-form-field
              appearance="outline"
              subscriptSizing="dynamic"
              class="r2m-settings-form__wide"
            >
              <textarea
                matInput
                cdkTextareaAutosize
                cdkAutosizeMinRows="2"
                [attr.aria-label]="field.label"
                [value]="stringValue(field)"
                [disabled]="disabled()"
                (input)="setValue(field, $any($event.target).value)"
              ></textarea>
            </mat-form-field>
          }
          @default {
            <mat-form-field
              appearance="outline"
              subscriptSizing="dynamic"
              class="r2m-settings-form__wide"
            >
              <input
                matInput
                type="text"
                [attr.aria-label]="field.label"
                [value]="stringValue(field)"
                [disabled]="disabled()"
                (input)="setValue(field, $any($event.target).value)"
              />
            </mat-form-field>
          }
        }

        @if (!fieldValid(field)) {
          <p class="r2m-settings-form__error" role="alert">
            Must be a number{{ field.min !== undefined ? ' ≥ ' + field.min : ''
            }}{{ field.max !== undefined ? ' ≤ ' + field.max : ''
            }}{{ field.nullable ? ', or blank' : '' }}.
          </p>
        } @else if (field.help) {
          <p class="r2m-settings-form__help">{{ field.help }}</p>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--r2m-space-4);
    }
    .r2m-settings-form__label-row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      min-height: 24px;
      margin-bottom: var(--r2m-space-1);
    }
    .r2m-settings-form__label {
      font-size: var(--r2m-text-sm);
      font-weight: 500;
      color: var(--r2m-text-muted);
    }
    .r2m-settings-form__field--overridden .r2m-settings-form__label {
      color: var(--r2m-text);
    }
    .r2m-settings-form__dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      border: 1px solid var(--r2m-outline);
    }
    .r2m-settings-form__dot--on {
      background: var(--r2m-accent);
      border-color: var(--r2m-accent);
    }
    .r2m-settings-form__reset {
      --mdc-icon-button-state-layer-size: 28px;
      width: 28px;
      height: 28px;
      padding: 2px;
    }
    .r2m-settings-form__reset mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .r2m-settings-form__slider-row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-3);
    }
    .r2m-settings-form__slider-row mat-slider {
      flex: 1;
    }
    .r2m-settings-form__number {
      width: 88px;
      height: 32px;
      padding: 0 var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-sm);
      background: var(--r2m-surface);
      color: var(--r2m-text);
      font: inherit;
      font-variant-numeric: tabular-nums;
    }
    .r2m-settings-form__field--invalid .r2m-settings-form__number {
      border-color: var(--r2m-status-error);
    }
    .r2m-settings-form__wide {
      width: 100%;
    }
    .r2m-settings-form__help,
    .r2m-settings-form__error {
      margin: var(--r2m-space-1) 0 0;
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
    }
    .r2m-settings-form__error {
      color: var(--r2m-status-error);
    }
  `,
})
export class SettingsForm {
  readonly schema = input.required<SettingsSchema>();
  readonly mode = input<SettingsFormMode>('defaults');
  readonly values = input<SettingsValues>({});
  readonly disabled = input(false, { transform: booleanAttribute });

  readonly valuesChange = output<SettingsValues>();
  readonly validChange = output<boolean>();

  /** Local working copy; re-seeded whenever the parent hands in new values. */
  private readonly working = signal<SettingsValues>({});

  readonly valid = computed(() =>
    this.schema().fields.every((f) => isFieldValid(f, effectiveValue(f, this.working()))),
  );

  constructor() {
    effect(() => {
      const incoming = this.values();
      untracked(() => this.working.set({ ...incoming }));
    });
    let last: boolean | undefined;
    effect(() => {
      const v = this.valid();
      if (v !== last) {
        last = v;
        this.validChange.emit(v);
      }
    });
  }

  protected value(field: SettingsField): unknown {
    return effectiveValue(field, this.working());
  }

  /** Blank for a nullable field left at null; the slider thumb falls back to the range start. */
  protected numberValue(field: SettingsField): number | string {
    const v = this.value(field);
    if (v == null) return field.nullable ? '' : 0;
    return typeof v === 'number' ? v : Number(v);
  }

  protected sliderValue(field: SettingsField): number {
    const v = this.numberValue(field);
    return typeof v === 'number' && !Number.isNaN(v) ? v : (field.min ?? 0);
  }

  protected stringValue(field: SettingsField): string {
    const v = this.value(field);
    return v == null ? '' : String(v);
  }

  protected isOverridden(field: SettingsField): boolean {
    return Object.prototype.hasOwnProperty.call(this.working(), field.key);
  }

  protected fieldValid(field: SettingsField): boolean {
    return isFieldValid(field, this.value(field));
  }

  /** Blank means null on a nullable field and "not a number" (invalid) on any other. */
  protected parseNumber(field: SettingsField, raw: string): number | null {
    if (raw.trim() === '') return field.nullable ? null : Number.NaN;
    return Number(raw);
  }

  protected setValue(field: SettingsField, value: unknown): void {
    this.working.update((w) => ({ ...w, [field.key]: value }));
    this.emitValues();
  }

  protected reset(field: SettingsField): void {
    this.working.update((w) => {
      const next = { ...w };
      delete next[field.key];
      return next;
    });
    this.emitValues();
  }

  private emitValues(): void {
    const schema = this.schema();
    const w = this.working();
    if (this.mode() === 'override') {
      this.valuesChange.emit(sparsePatch(schema, w));
      return;
    }
    const full: SettingsValues = {};
    for (const field of schema.fields) full[field.key] = effectiveValue(field, w);
    this.valuesChange.emit(full);
  }
}
