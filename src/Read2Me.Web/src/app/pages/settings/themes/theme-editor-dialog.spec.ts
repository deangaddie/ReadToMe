import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { AppTheme } from '@app/api';
import { PALETTE_DEFAULTS } from '../../../../testing/themes';
import { override, resetServices } from '@app/core/services';
import { ConfirmService, type ConfirmOptions } from '@app/ui/dialogs';
import { settle } from '../../../../testing/fake-navigation';
import {
  type ThemeEditorDialog,
  type ThemeEditorResult,
  openThemeEditorDialog,
} from './theme-editor-dialog';
import './theme-editor-dialog';

/** The editor's content element on its own: the dialog chrome is the browser's and E2E's. */
const orange: AppTheme = {
  ...PALETTE_DEFAULTS,
  id: 10,
  name: 'Orange',
  isBuiltIn: false,
  isDark: true,
  primary: '#FF9900',
  secondary: '#00aaff',
  surface: '#222222',
};

let closed: (ThemeEditorResult | undefined)[];

beforeEach(() => {
  resetServices();
  closed = [];
});
afterEach(() => document.body.replaceChildren());

async function mount(theme?: AppTheme) {
  const el = document.createElement('r2m-theme-editor-dialog') as ThemeEditorDialog;
  el.data = theme ? { theme } : {};
  el.addEventListener('r2m-close', (e) =>
    closed.push((e as CustomEvent<ThemeEditorResult>).detail),
  );
  document.body.append(el);
  await el.rendered();
  return el;
}

const hex = (el: Element, label: string) =>
  el.querySelector<HTMLInputElement>(`input[aria-label="${label} hex"]`)!;
const save = (el: Element) => el.querySelector<HTMLButtonElement>('[data-action="save-theme"]')!;

async function type(el: ThemeEditorDialog, input: HTMLInputElement, value: string) {
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await el.rendered();
}

describe('r2m-theme-editor-dialog', () => {
  it('New starts from the defaults with Create disabled until a name is typed', async () => {
    const el = await mount();
    expect(el.querySelector('.r2m-dialog__title')?.textContent).toBe('New theme');
    expect(save(el).textContent?.trim()).toBe('Create');
    expect(save(el).disabled).toBe(true);
    expect(el.querySelector('[data-role="advanced"]')).toBeNull();

    await type(el, el.querySelector<HTMLInputElement>('input[name="name"]')!, 'Sunset');
    expect(save(el).disabled).toBe(false);

    save(el).click();
    expect(closed).toEqual([
      {
        name: 'Sunset',
        isDark: false,
        primary: '#594ae2',
        secondary: '#ff4081',
        background: null,
        surface: null,
        appbarBackground: null,
        drawerBackground: null,
        textPrimary: null,
        textSecondary: null,
      },
    ]);
  });

  it('a custom theme edits in place with its advanced colours shown; a bad hex blocks Save', async () => {
    const el = await mount(orange);
    expect(el.querySelector('.r2m-dialog__title')?.textContent).toBe('Edit Orange');
    expect(save(el).textContent?.trim()).toBe('Save');
    expect(el.querySelector('[data-role="advanced"]')).not.toBeNull();
    expect(hex(el, 'Primary').value).toBe('#ff9900');
    expect(hex(el, 'Surface').value).toBe('#222222');

    await type(el, hex(el, 'Primary'), 'orange');
    expect(el.querySelector('[data-colour="primary"] [role="alert"]')?.textContent).toBe(
      'Use #rgb or #rrggbb',
    );
    expect(save(el).disabled).toBe(true);

    await type(el, hex(el, 'Primary'), '#F90');
    el.querySelector<HTMLButtonElement>('button[aria-label="Clear Surface"]')!.click();
    await el.rendered();
    expect(hex(el, 'Surface').value).toBe('');
    save(el).click();
    expect(closed[0]).toMatchObject({ name: 'Orange', primary: '#ff9900', surface: null });
  });

  it('a built-in opens as a copy named after it', async () => {
    const el = await mount({ ...orange, id: 1, name: 'Light', isBuiltIn: true });
    expect(el.querySelector('.r2m-dialog__title')?.textContent).toBe('Copy of Light');
    expect(el.querySelector<HTMLInputElement>('input[name="name"]')!.value).toBe('Light copy');
    expect(save(el).textContent?.trim()).toBe('Create');
  });

  it('the advanced toggle reveals the optional colours', async () => {
    const el = await mount();
    el.querySelector<HTMLButtonElement>('[data-action="toggle-advanced"]')!.click();
    await el.rendered();
    expect(el.querySelectorAll('[data-role="advanced"] .editor__colour').length).toBe(6);
  });

  describe('closing with unsaved changes', () => {
    let asked: ConfirmOptions[];
    let discard: boolean;

    beforeEach(() => {
      asked = [];
      discard = false;
      override(ConfirmService, {
        confirm: async (options: ConfirmOptions) => {
          asked.push(options);
          return discard;
        },
      } as unknown as ConfirmService);
    });

    async function open(theme?: AppTheme) {
      const result = openThemeEditorDialog(theme ? { theme } : {});
      await Promise.resolve();
      const el = document.querySelector<ThemeEditorDialog>('r2m-theme-editor-dialog')!;
      await el.rendered();
      return { result, el };
    }

    const cancel = (el: Element) => el.querySelector<HTMLButtonElement>('.editor__cancel')!.click();
    const escape = (el: Element) =>
      el.closest('dialog')!.dispatchEvent(new Event('cancel', { cancelable: true }));

    it('Cancel and Escape close without asking while nothing has changed', async () => {
      const first = await open(orange);
      cancel(first.el);
      expect(await first.result).toBeNull();

      const second = await open();
      escape(second.el);
      expect(await second.result).toBeNull();
      expect(asked).toEqual([]);
    });

    it('asks before Cancel or Escape discard a changed draft, and Keep stays open', async () => {
      const { result, el } = await open(orange);
      await type(el, el.querySelector<HTMLInputElement>('input[name="name"]')!, 'Tangerine');

      cancel(el);
      await settle();
      escape(el);
      await settle();
      expect(asked.map((o) => [o.title, o.destructive])).toEqual([
        ['Discard changes?', true],
        ['Discard changes?', true],
      ]);
      expect(el.closest('dialog')?.open).toBe(true);
      expect(el.draft().name).toBe('Tangerine');

      discard = true;
      escape(el);
      expect(await result).toBeNull();
    });

    it('a change typed back to how it opened counts as nothing changed', async () => {
      const { result, el } = await open();
      const name = el.querySelector<HTMLInputElement>('input[name="name"]')!;
      await type(el, name, 'Sunset');
      await type(el, name, '');
      cancel(el);
      expect(await result).toBeNull();
      expect(asked).toEqual([]);
    });
  });
});
