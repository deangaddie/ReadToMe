import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  SettingsForm,
  SettingsFormMode,
  SettingsSchema,
  SettingsValues,
  isFieldValid,
  sparsePatch,
} from './settings-form';

const SCHEMA: SettingsSchema = {
  type: 'VoxCpm2',
  fields: [
    {
      key: 'cfg',
      label: 'CFG',
      kind: 'number',
      min: 0,
      max: 5,
      step: 0.1,
      default: 2,
      help: 'Guidance',
    },
    { key: 'seed', label: 'Seed', kind: 'number', default: 0 },
    { key: 'denoise', label: 'Denoise', kind: 'boolean', default: true },
    {
      key: 'lang',
      label: 'Language',
      kind: 'enum',
      options: [{ value: 'en', label: 'English' }, { value: 'fr' }],
      default: 'en',
    },
    { key: 'name', label: 'Name', kind: 'string', default: '' },
    { key: 'notes', label: 'Notes', kind: 'text', default: '' },
    { key: 'topK', label: 'Top K', kind: 'number', min: 1, default: null, nullable: true },
  ],
};

@Component({
  imports: [SettingsForm],
  template: `<r2m-settings-form
    [schema]="schema"
    [mode]="mode()"
    [values]="values()"
    (valuesChange)="emitted.push($event)"
    (validChange)="valid = $event"
  />`,
})
class HostCmp {
  schema = SCHEMA;
  readonly mode = signal<SettingsFormMode>('defaults');
  readonly values = signal<SettingsValues>({});
  emitted: SettingsValues[] = [];
  valid: boolean | null = null;
}

function field(fixture: { nativeElement: HTMLElement }, key: string): HTMLElement {
  return fixture.nativeElement.querySelector(`[data-key="${key}"]`)!;
}

function typeNumber(el: HTMLElement, selector: string, value: string) {
  const input = el.querySelector(selector) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('settings-form helpers', () => {
  it('sparsePatch keeps only keys that differ from the default', () => {
    expect(sparsePatch(SCHEMA, { cfg: 2, seed: 7, denoise: true })).toEqual({ seed: 7 });
  });

  it('isFieldValid checks numbers against range', () => {
    const cfg = SCHEMA.fields[0]!;
    expect(isFieldValid(cfg, 3)).toBe(true);
    expect(isFieldValid(cfg, 9)).toBe(false);
    expect(isFieldValid(cfg, Number.NaN)).toBe(false);
    expect(isFieldValid(SCHEMA.fields[2]!, 'anything')).toBe(true);
  });

  it('isFieldValid accepts null only on a nullable number', () => {
    const topK = SCHEMA.fields.find((f) => f.key === 'topK')!;
    expect(isFieldValid(topK, null)).toBe(true);
    expect(isFieldValid(topK, 0)).toBe(false);
    expect(isFieldValid(SCHEMA.fields[1]!, null)).toBe(false);
  });
});

describe('r2m-settings-form', () => {
  async function mount(mode: SettingsFormMode = 'defaults', values: SettingsValues = {}) {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    fixture.componentInstance.mode.set(mode);
    fixture.componentInstance.values.set(values);
    await fixture.whenStable();
    return fixture;
  }

  it('renders every field kind', async () => {
    const fixture = await mount();
    expect(field(fixture, 'cfg').querySelector('mat-slider')).not.toBeNull();
    expect(field(fixture, 'cfg').querySelector('.r2m-settings-form__number')).not.toBeNull();
    expect(field(fixture, 'seed').querySelector('mat-slider')).toBeNull();
    expect(field(fixture, 'seed').querySelector('input[type="number"]')).not.toBeNull();
    expect(field(fixture, 'denoise').querySelector('mat-slide-toggle')).not.toBeNull();
    expect(field(fixture, 'lang').querySelector('mat-select')).not.toBeNull();
    expect(field(fixture, 'name').querySelector('input[type="text"]')).not.toBeNull();
    expect(field(fixture, 'notes').querySelector('textarea')).not.toBeNull();
    expect(field(fixture, 'cfg').querySelector('.r2m-settings-form__help')?.textContent).toBe(
      'Guidance',
    );
    expect(fixture.componentInstance.valid).toBe(true);
  });

  it('defaults mode emits the full value set', async () => {
    const fixture = await mount('defaults', { cfg: 1.5 });
    typeNumber(field(fixture, 'seed'), 'input[type="number"]', '42');
    expect(fixture.componentInstance.emitted.at(-1)).toEqual({
      cfg: 1.5,
      seed: 42,
      denoise: true,
      lang: 'en',
      name: '',
      notes: '',
      topK: null,
    });
  });

  it('a nullable number renders blank at null and blank means null again', async () => {
    const fixture = await mount('override', { topK: 40 });
    const input = field(fixture, 'topK').querySelector('input[type="number"]') as HTMLInputElement;
    expect(input.value).toBe('40');
    typeNumber(field(fixture, 'topK'), 'input[type="number"]', '');
    await fixture.whenStable();
    expect(fixture.componentInstance.emitted.at(-1)).toEqual({});
    expect(fixture.componentInstance.valid).toBe(true);
  });

  it('override mode emits a sparse patch, shows the dot, and reset removes the key', async () => {
    const fixture = await mount('override', { cfg: 3 });
    expect(field(fixture, 'cfg').classList.contains('r2m-settings-form__field--overridden')).toBe(
      true,
    );
    expect(field(fixture, 'seed').classList.contains('r2m-settings-form__field--overridden')).toBe(
      false,
    );
    expect(field(fixture, 'cfg').querySelector('.r2m-settings-form__dot--on')).not.toBeNull();

    typeNumber(field(fixture, 'seed'), 'input[type="number"]', '9');
    expect(fixture.componentInstance.emitted.at(-1)).toEqual({ cfg: 3, seed: 9 });

    // Setting a field back to its default drops it from the patch.
    typeNumber(field(fixture, 'seed'), 'input[type="number"]', '0');
    expect(fixture.componentInstance.emitted.at(-1)).toEqual({ cfg: 3 });

    (field(fixture, 'cfg').querySelector('.r2m-settings-form__reset') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(fixture.componentInstance.emitted.at(-1)).toEqual({});
    expect(field(fixture, 'cfg').classList.contains('r2m-settings-form__field--overridden')).toBe(
      false,
    );
  });

  it('flips validChange false on an out-of-range number and back on fix', async () => {
    const fixture = await mount();
    typeNumber(field(fixture, 'cfg'), '.r2m-settings-form__number', '99');
    await fixture.whenStable();
    expect(fixture.componentInstance.valid).toBe(false);
    expect(field(fixture, 'cfg').querySelector('.r2m-settings-form__error')?.textContent).toContain(
      '≤ 5',
    );

    typeNumber(field(fixture, 'cfg'), '.r2m-settings-form__number', '4');
    await fixture.whenStable();
    expect(fixture.componentInstance.valid).toBe(true);
  });
});
