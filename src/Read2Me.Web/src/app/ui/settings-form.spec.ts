import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import {
  type SettingsFormMode,
  type SettingsSchema,
  type SettingsValues,
  isFieldValid,
  sparsePatch,
} from './settings-form';
import './settings-form';
import { blockedWith, description } from '../../testing/description';
import { FakeTimers } from '../../testing/fake-timers';
import { ERROR_ANNOUNCE_DELAY_MS } from './field';

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

afterEach(() => document.body.replaceChildren());

async function mount(mode: SettingsFormMode = 'defaults', values: SettingsValues = {}) {
  const form = document.createElement('r2m-settings-form');
  form.schema = SCHEMA;
  form.mode = mode;
  form.values = values;
  const emitted: SettingsValues[] = [];
  const valid: boolean[] = [];
  form.addEventListener('values-change', (e) =>
    emitted.push((e as CustomEvent<SettingsValues>).detail),
  );
  form.addEventListener('valid-change', (e) => valid.push((e as CustomEvent<boolean>).detail));
  document.body.append(form);
  await form.rendered();
  return { form, emitted, valid };
}

const field = (form: HTMLElement, key: string) =>
  form.querySelector<HTMLElement>(`[data-key="${key}"]`)!;

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
  it('renders every field kind', async () => {
    const { form, valid } = await mount();
    expect(field(form, 'cfg').querySelector('input[type="range"]')).not.toBeNull();
    expect(field(form, 'cfg').querySelector('.r2m-settings-form__number')).not.toBeNull();
    expect(field(form, 'seed').querySelector('input[type="range"]')).toBeNull();
    expect(field(form, 'seed').querySelector('input[type="number"]')).not.toBeNull();
    expect(field(form, 'denoise').querySelector('input[role="switch"]')).not.toBeNull();
    expect(field(form, 'lang').querySelector('select')).not.toBeNull();
    expect(field(form, 'name').querySelector('input[type="text"]')).not.toBeNull();
    expect(field(form, 'notes').querySelector('textarea')).not.toBeNull();
    expect(field(form, 'cfg').querySelector('.r2m-settings-form__help')?.textContent).toBe(
      'Guidance',
    );
    expect(valid.at(-1)).toBe(true);
  });

  it('names each control by a label of its own, and the slider by “… slider”', async () => {
    const { form } = await mount();
    const named = {
      cfg: '.r2m-settings-form__number',
      seed: 'input[type="number"]',
      denoise: 'input[role="switch"]',
      lang: 'select',
      name: 'input[type="text"]',
      notes: 'textarea',
    };
    const ids = new Set<string>();
    for (const [key, selector] of Object.entries(named)) {
      const control = field(form, key).querySelector(selector)!;
      const label = field(form, key).querySelector<HTMLLabelElement>('.r2m-settings-form__label')!;
      expect(label.tagName).toBe('LABEL');
      expect(control.id).not.toBe('');
      expect(label.htmlFor).toBe(control.id);
      expect(control.hasAttribute('aria-label')).toBe(false);
      ids.add(control.id);
    }
    expect(ids.size).toBe(Object.keys(named).length);
    expect(
      field(form, 'cfg').querySelector('input[type="range"]')?.getAttribute('aria-label'),
    ).toBe('CFG slider');
  });

  it('a second form on the page gives its controls other ids', async () => {
    const { form: one } = await mount();
    const { form: two } = await mount();
    const id = (form: HTMLElement) => field(form, 'seed').querySelector('input')!.id;
    expect(id(one)).not.toBe(id(two));
  });

  it('clicking a switch field’s name toggles it', async () => {
    const { form, emitted } = await mount('override');
    field(form, 'denoise')
      .querySelector<HTMLLabelElement>('label.r2m-settings-form__label')!
      .click();
    expect(emitted.at(-1)).toEqual({ denoise: false });
  });

  it('turns text correction off on a string field and leaves a text field alone', async () => {
    const { form } = await mount();
    const string = field(form, 'name').querySelector('input')!;
    expect(string.getAttribute('spellcheck')).toBe('false');
    expect(string.getAttribute('autocapitalize')).toBe('none');
    expect(string.getAttribute('autocorrect')).toBe('off');
    const text = field(form, 'notes').querySelector('textarea')!;
    for (const name of ['spellcheck', 'autocapitalize', 'autocorrect']) {
      expect(text.hasAttribute(name)).toBe(false);
    }
  });

  it('defaults mode emits the full value set', async () => {
    const { form, emitted } = await mount('defaults', { cfg: 1.5 });
    typeNumber(field(form, 'seed'), 'input[type="number"]', '42');
    expect(emitted.at(-1)).toEqual({
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
    const { form, emitted, valid } = await mount('override', { topK: 40 });
    const input = field(form, 'topK').querySelector('input[type="number"]') as HTMLInputElement;
    expect(input.value).toBe('40');
    typeNumber(field(form, 'topK'), 'input[type="number"]', '');
    await form.rendered();
    expect(emitted.at(-1)).toEqual({});
    expect(valid.at(-1)).toBe(true);
  });

  it('override mode emits a sparse patch, shows the dot, and reset removes the key', async () => {
    const { form, emitted } = await mount('override', { cfg: 3 });
    expect(field(form, 'cfg').classList.contains('r2m-settings-form__field--overridden')).toBe(
      true,
    );
    expect(field(form, 'seed').classList.contains('r2m-settings-form__field--overridden')).toBe(
      false,
    );
    expect(field(form, 'cfg').querySelector('.r2m-settings-form__dot--on')).not.toBeNull();

    typeNumber(field(form, 'seed'), 'input[type="number"]', '9');
    expect(emitted.at(-1)).toEqual({ cfg: 3, seed: 9 });

    // Setting a field back to its default drops it from the patch.
    typeNumber(field(form, 'seed'), 'input[type="number"]', '0');
    expect(emitted.at(-1)).toEqual({ cfg: 3 });

    await form.rendered();
    (field(form, 'cfg').querySelector('.r2m-settings-form__reset') as HTMLButtonElement).click();
    await form.rendered();
    expect(emitted.at(-1)).toEqual({});
    expect(field(form, 'cfg').classList.contains('r2m-settings-form__field--overridden')).toBe(
      false,
    );
  });

  it('flips valid-change false on an out-of-range number and back on fix, while typing', async () => {
    const { form, valid } = await mount();
    typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '99');
    await form.rendered();
    expect(valid.at(-1)).toBe(false);

    typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '4');
    await form.rendered();
    expect(valid.at(-1)).toBe(true);
  });

  describe('a number’s error', () => {
    let timers: FakeTimers;
    let said: string[];

    beforeEach(() => {
      timers = new FakeTimers().install();
      said = [];
      document.ariaNotify = (text: string) => void said.push(text);
    });

    afterEach(() => {
      timers.restore();
      delete document.ariaNotify;
    });

    const number = (form: HTMLElement) =>
      field(form, 'cfg').querySelector<HTMLInputElement>('.r2m-settings-form__number')!;
    const error = (form: HTMLElement) =>
      field(form, 'cfg').querySelector<HTMLElement>('.r2m-settings-form__error');
    const leave = (form: HTMLElement) =>
      number(form).dispatchEvent(new FocusEvent('blur')) &&
      number(form).dispatchEvent(new FocusEvent('focusout', { bubbles: true }));

    it('stays hidden while the user types, and the control reads as valid', async () => {
      const { form } = await mount();
      typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '-');
      await form.rendered();
      await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
      expect(error(form)).toBeNull();
      expect(number(form).getAttribute('aria-invalid')).toBe('false');
      expect(number(form).hasAttribute('aria-describedby')).toBe(false);
      expect(said).toEqual([]);
    });

    it('shows on blur, describes the control, marks it invalid and is announced once', async () => {
      const { form } = await mount();
      typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '99');
      leave(form);
      await form.rendered();
      const shown = error(form)!;
      expect(shown.hasAttribute('role')).toBe(false);
      expect(shown.id).not.toBe('');
      expect(number(form).getAttribute('aria-describedby')).toBe(shown.id);
      expect(number(form).getAttribute('aria-invalid')).toBe('true');
      expect(field(form, 'cfg').querySelector('.r2m-settings-form__help')).toBeNull();

      await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
      expect(said).toEqual(['CFG: Must be a number ≥ 0 ≤ 5.']);
      leave(form);
      await form.rendered();
      await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
      expect(said).toHaveLength(1);
    });

    it('clears as the value turns valid, and stays away while it is retyped wrong', async () => {
      const { form } = await mount();
      typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '99');
      leave(form);
      await form.rendered();
      expect(error(form)).not.toBeNull();

      typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '4');
      await form.rendered();
      expect(error(form)).toBeNull();
      expect(number(form).getAttribute('aria-invalid')).toBe('false');
      expect(number(form).hasAttribute('aria-describedby')).toBe(false);

      typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '-');
      await form.rendered();
      expect(error(form)).toBeNull();
    });

    it('is read after a Blocked reason while both show', async () => {
      const { form } = await mount();
      typeNumber(field(form, 'cfg'), '.r2m-settings-form__number', '99');
      leave(form);
      form.block = { reason: 'Viewing a past step' };
      await form.rendered();
      expect(description(number(form))?.replace(/\s+/g, ' ').trim()).toBe(
        'Viewing a past step Must be a number ≥ 0 ≤ 5.',
      );
    });
  });

  const CONTROLS = 'input, select, textarea, button';

  it('a gate with a reason keeps every control focusable, and says why on each', async () => {
    const { form, emitted } = await mount('override', { cfg: 3 });
    form.block = { reason: 'Viewing a past step' };
    await form.rendered();

    const controls = form.querySelectorAll<HTMLInputElement>(CONTROLS);
    expect(controls.length).toBeGreaterThan(SCHEMA.fields.length);
    for (const control of controls) {
      expect(control.disabled).toBe(false);
      expect(blockedWith(control)).toBe('Viewing a past step');
      expect(control.getAttribute('data-tooltip')).toBe('Viewing a past step');
    }
    for (const key of SCHEMA.fields.map((f) => f.key)) {
      expect(field(form, key).getAttribute('data-tooltip')).toBe('Viewing a past step');
      expect(field(form, key).hasAttribute('tabindex')).toBe(false);
    }
    field(form, 'cfg').querySelector<HTMLButtonElement>('.r2m-settings-form__reset')!.click();
    field(form, 'denoise').querySelector<HTMLInputElement>('input')!.click();
    typeNumber(field(form, 'seed'), 'input', '9');
    expect(emitted).toEqual([]);

    form.block = null;
    await form.rendered();
    for (const control of form.querySelectorAll(CONTROLS)) {
      expect(control.hasAttribute('aria-disabled')).toBe(false);
      expect(control.hasAttribute('aria-describedby')).toBe(false);
      expect(control.hasAttribute('readonly')).toBe(false);
    }
    const reset = field(form, 'cfg').querySelector('.r2m-settings-form__reset')!;
    expect(reset.getAttribute('data-tooltip')).toBe('Reset to default');
    expect(field(form, 'cfg').hasAttribute('data-tooltip')).toBe(false);
  });

  it('a busy form is plain disabled, with no reason to give', async () => {
    const { form } = await mount('override', { cfg: 3 });
    form.disabled = true;
    await form.rendered();

    for (const control of form.querySelectorAll<HTMLInputElement>(CONTROLS)) {
      expect(control.disabled).toBe(true);
      expect(control.hasAttribute('aria-disabled')).toBe(false);
    }
    expect(field(form, 'cfg').hasAttribute('data-tooltip')).toBe(false);
  });

  it('new values from the host re-seed the working copy', async () => {
    const { form } = await mount('override', { cfg: 3 });
    form.values = {};
    await form.rendered();
    expect(field(form, 'cfg').querySelector('.r2m-settings-form__dot--on')).toBeNull();
    expect(
      (field(form, 'cfg').querySelector('.r2m-settings-form__number') as HTMLInputElement).value,
    ).toBe('2');
  });

  it('the slider and the number box move together; a switch and a select set their values', async () => {
    const { form, emitted } = await mount('override');
    const range = field(form, 'cfg').querySelector('input[type="range"]') as HTMLInputElement;
    range.value = '4.5';
    range.dispatchEvent(new Event('input'));
    await form.rendered();
    expect(emitted.at(-1)).toEqual({ cfg: 4.5 });
    expect(
      (field(form, 'cfg').querySelector('.r2m-settings-form__number') as HTMLInputElement).value,
    ).toBe('4.5');

    const toggle = field(form, 'denoise').querySelector('input[role="switch"]') as HTMLInputElement;
    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    expect(emitted.at(-1)).toEqual({ cfg: 4.5, denoise: false });

    const select = field(form, 'lang').querySelector('select') as HTMLSelectElement;
    select.value = 'fr';
    select.dispatchEvent(new Event('change'));
    expect(emitted.at(-1)).toEqual({ cfg: 4.5, denoise: false, lang: 'fr' });
    expect(form.querySelector('[data-key="lang"] option:nth-child(2)')?.textContent?.trim()).toBe(
      'fr',
    );
  });
});
