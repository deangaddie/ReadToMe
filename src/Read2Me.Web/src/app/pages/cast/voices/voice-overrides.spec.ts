import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { override, resetServices } from '@app/core/services';
import type { SettingsSchema } from '@app/ui/settings-form';
import { settle } from '../../../../testing/fake-navigation';
import { ProviderSchemas, type ProviderArea } from './provider-schemas';
import type { OverrideSave } from './voice-overrides';
import './voice-overrides';

const SCHEMA: SettingsSchema = {
  type: 'VoxCpm2',
  fields: [
    { key: 'cfg_value', label: 'CFG', kind: 'number', min: 1, max: 5, step: 0.1, default: 2 },
    { key: 'denoise', label: 'Denoise', kind: 'boolean', default: false },
  ],
};

let schemas: Record<ProviderArea, () => Promise<SettingsSchema | null>>;

beforeEach(() => {
  resetServices();
  schemas = {
    'voice-design': async () => SCHEMA,
    'paragraph-tts': async () => null,
  };
  override(ProviderSchemas, {
    active: (area: ProviderArea) => schemas[area](),
  } as unknown as ProviderSchemas);
});
afterEach(() => document.body.replaceChildren());

async function mount(area: ProviderArea, storedJson: string | null = null) {
  const editor = document.createElement('r2m-voice-override-editor');
  editor.area = area;
  editor.storedJson = storedJson;
  const saves: OverrideSave[] = [];
  editor.addEventListener('save', (e) => saves.push((e as CustomEvent<OverrideSave>).detail));
  document.body.append(editor);
  await editor.rendered();
  await settle();
  await editor.rendered();
  return { editor, saves };
}

const save = (editor: Element) =>
  editor.querySelector<HTMLButtonElement>('[data-action=save-override]')!;
const field = (editor: Element, key: string) =>
  editor.querySelector<HTMLElement>(`[data-key="${key}"]`)!;

describe('r2m-voice-override-editor', () => {
  it('loads the active schema, names the provider and seeds the form from the stored column', async () => {
    const { editor } = await mount('voice-design', '{"cfg_value":3.5}');
    expect(editor.getAttribute('data-area')).toBe('voice-design');
    expect(editor.querySelector('.voice-override__note')?.textContent).toContain(
      'Overriding the active voice design provider (VoxCpm2)',
    );
    expect(field(editor, 'cfg_value').querySelector('.r2m-settings-form__dot--on')).not.toBeNull();
    expect(
      field(editor, 'cfg_value').querySelector<HTMLInputElement>('.r2m-settings-form__number')
        ?.value,
    ).toBe('3.5');
    expect(save(editor).disabled).toBe(true);
  });

  it('Save is dirty-gated and emits the sparse patch; reset to default emits null', async () => {
    const { editor, saves } = await mount('voice-design', '{"cfg_value":3.5}');
    const number = field(editor, 'cfg_value').querySelector<HTMLInputElement>(
      '.r2m-settings-form__number',
    )!;
    number.value = '4';
    number.dispatchEvent(new Event('input'));
    await editor.rendered();
    expect(save(editor).disabled).toBe(false);
    save(editor).click();
    expect(saves).toEqual([{ area: 'voice-design', json: '{"cfg_value":4}' }]);

    field(editor, 'cfg_value')
      .querySelector<HTMLButtonElement>('.r2m-settings-form__reset')!
      .click();
    await editor.rendered();
    save(editor).click();
    expect(saves.at(-1)).toEqual({ area: 'voice-design', json: null });

    // A reload of the voice re-seeds the working patch, so the button goes quiet again.
    editor.storedJson = null;
    await editor.rendered();
    expect(save(editor).disabled).toBe(true);
  });

  it('an invalid field blocks Save', async () => {
    const { editor } = await mount('voice-design');
    const number = field(editor, 'cfg_value').querySelector<HTMLInputElement>(
      '.r2m-settings-form__number',
    )!;
    number.value = '99';
    number.dispatchEvent(new Event('input'));
    await editor.rendered();
    expect(editor.querySelector('.r2m-settings-form__error')).not.toBeNull();
    expect(save(editor).disabled).toBe(true);
  });

  it('says so when no provider is active, and shows a failed load', async () => {
    const none = await mount('paragraph-tts');
    expect(none.editor.querySelector('.voice-override__note')?.textContent).toContain(
      'No active text-to-speech provider — nothing to override.',
    );
    expect(none.editor.querySelector('r2m-settings-form')).toBeNull();

    document.body.replaceChildren();
    schemas['voice-design'] = () => Promise.reject(new Error('schema down'));
    const failed = await mount('voice-design');
    expect(failed.editor.querySelector('[role=alert]')?.textContent).toContain('schema down');
  });
});
