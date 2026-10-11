import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { AppTheme } from '@app/api';
import { resetServices } from '@app/core/services';
import type { ThemeEditorDialog, ThemeEditorResult } from './theme-editor-dialog';
import './theme-editor-dialog';

/** The editor's content element on its own: the dialog chrome is the browser's and E2E's. */
const orange: AppTheme = {
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

  it('Cancel closes with nothing; the advanced toggle reveals the optional colours', async () => {
    const el = await mount();
    el.querySelector<HTMLButtonElement>('[data-action="toggle-advanced"]')!.click();
    await el.rendered();
    expect(el.querySelectorAll('[data-role="advanced"] .editor__colour').length).toBe(6);

    el.querySelector<HTMLButtonElement>('.editor__cancel')!.click();
    // No detail on Cancel: the event carries null, which `openDialog` resolves as a dismissal.
    expect(closed).toEqual([null as unknown as ThemeEditorResult]);
  });
});
