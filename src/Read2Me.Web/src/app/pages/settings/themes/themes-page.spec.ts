import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { AppTheme } from '@app/api';
import { PALETTE_DEFAULTS } from '../../../../testing/themes';
import { override, resetServices, use } from '@app/core/services';
import { ThemeService } from '@app/theme/theme.service';
import { ConfirmService, type ConfirmOptions } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../../testing/fake-api';
import { settle } from '../../../../testing/fake-navigation';
import { ThemeEditor, type ThemeEditorData, type ThemeEditorResult } from './theme-editor-dialog';
import type { ThemesPage } from './themes-page';
import './themes-page';

/** Ported from the Angular TestBed spec, case for case, over `FakeApi` and the booted `ThemeService`. */
const THEMES = '/api/settings/themes';
const SELECTION = `${THEMES}/selection`;

const light: AppTheme = {
  ...PALETTE_DEFAULTS,
  id: 1,
  name: 'Light',
  isBuiltIn: true,
  isDark: false,
  primary: '#594AE2',
  secondary: '#FF4081',
};
const orange: AppTheme = {
  ...PALETTE_DEFAULTS,
  id: 10,
  name: 'Orange',
  isBuiltIn: false,
  isDark: true,
  primary: '#ff9900',
  secondary: '#00aaff',
  surface: '#222222',
};

let api: FakeApi;
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;
let toasts: string[];

beforeEach(async () => {
  resetServices();
  api = new FakeApi();
  api.install();
  confirms = [];
  confirmAnswer = true;
  toasts = [];
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    problem: () => undefined,
  } as unknown as ToastService);
  api.on('GET', THEMES, [light, orange]);
  api.on('GET', SELECTION, { selectedThemeId: 10, followSystemPreference: false });
  await use(ThemeService).load();
});
afterEach(() => {
  document.body.replaceChildren();
  expect(api.unrouted).toEqual([]);
});

async function flush(page: ThemesPage) {
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
  await page.rendered();
}

async function mount(editor?: (data: ThemeEditorData) => Promise<ThemeEditorResult | null>) {
  if (editor) override(ThemeEditor, { open: editor } as unknown as ThemeEditor);
  const page = document.createElement('r2m-themes-page') as ThemesPage;
  document.body.append(page);
  await flush(page);
  return page;
}

const card = (page: Element, id: number) => page.querySelector(`[data-theme-id="${id}"]`)!;

describe('r2m-themes-page', () => {
  it('renders a card per theme with Active marker, lock and delete only where allowed', async () => {
    const page = await mount();

    expect(page.querySelectorAll('.themes__card').length).toBe(2);
    const lightCard = card(page, 1);
    const orangeCard = card(page, 10);
    expect(lightCard.querySelector('.themes__lock')).not.toBeNull();
    expect(lightCard.querySelector('.themes__delete')).toBeNull();
    expect(orangeCard.querySelector('.themes__delete')).not.toBeNull();
    expect(orangeCard.textContent).toContain('Active');
    expect(orangeCard.classList.contains('themes__card--selected')).toBe(true);
    expect(orangeCard.querySelectorAll('.themes__swatch').length).toBe(3);
    expect(orangeCard.querySelector<HTMLButtonElement>('[data-action="apply"]')!.disabled).toBe(
      true,
    );
    expect(orangeCard.querySelector('[data-action="apply"]')?.textContent?.trim()).toBe('Applied');
  });

  it('Apply writes the selection through the service', async () => {
    const page = await mount();

    api.on('PUT', SELECTION, { selectedThemeId: 1, followSystemPreference: false });
    card(page, 1).querySelector<HTMLButtonElement>('[data-action="apply"]')!.click();
    await flush(page);

    expect(api.calls('PUT', SELECTION)[0]?.body).toEqual({
      selectedThemeId: 1,
      followSystemPreference: false,
    });
    expect(card(page, 1).textContent).toContain('Active');
    expect(card(page, 10).textContent).not.toContain('Active');
    expect(document.documentElement.dataset['theme']).toBe('light');
    expect(toasts).toEqual(['Applied Light']);
  });

  it('follow-system toggle flips only the flag', async () => {
    const page = await mount();

    api.on('PUT', SELECTION, { selectedThemeId: 10, followSystemPreference: true });
    const toggle = page.querySelector<HTMLInputElement>('input[name="followSystem"]')!;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    await flush(page);

    expect(api.calls('PUT', SELECTION)[0]?.body).toEqual({ followSystemPreference: true });
    expect(page.followSystem()).toBe(true);
    expect(toggle.checked).toBe(true);
  });

  it('New theme creates what the editor returns and reloads the list', async () => {
    const created: ThemeEditorResult = {
      name: 'Sunset',
      isDark: false,
      primary: '#ff6f61',
      secondary: '#ffbb33',
      background: null,
      surface: null,
      appbarBackground: null,
      drawerBackground: null,
      textPrimary: null,
      textSecondary: null,
    };
    const opened: ThemeEditorData[] = [];
    const page = await mount(async (data) => {
      opened.push(data);
      return created;
    });

    api.on('POST', THEMES, { ...created, id: 11, isBuiltIn: false });
    api.on('GET', THEMES, [light, orange, { ...created, id: 11, isBuiltIn: false }]);
    page.querySelector<HTMLButtonElement>('[data-action="new-theme"]')!.click();
    await flush(page);

    expect(opened).toEqual([{}]);
    expect(api.calls('POST', THEMES)[0]?.body).toEqual({ ...created, id: 0, isBuiltIn: false });
    expect(page.querySelectorAll('.themes__card').length).toBe(3);
    expect(toasts).toEqual(['Created Sunset']);
  });

  it('editing a built-in creates a copy; editing a custom theme updates it in place', async () => {
    const opened: ThemeEditorData[] = [];
    const page = await mount(async (data) => {
      opened.push(data);
      const theme = data.theme!;
      return {
        name: `${theme.name} edited`,
        isDark: theme.isDark,
        primary: theme.primary,
        secondary: theme.secondary,
        background: null,
        surface: null,
        appbarBackground: null,
        drawerBackground: null,
        textPrimary: null,
        textSecondary: null,
      };
    });

    api.on('POST', THEMES, { id: 12 });
    card(page, 1).querySelector<HTMLButtonElement>('[data-action="edit"]')!.click();
    await flush(page);
    expect(opened[0]?.theme).toEqual(light);
    expect(api.calls('POST', THEMES)[0]?.body).toMatchObject({ name: 'Light edited' });

    api.on('PUT', `${THEMES}/10`, { id: 10 });
    card(page, 10).querySelector<HTMLButtonElement>('[data-action="edit"]')!.click();
    await flush(page);
    expect(api.calls('PUT', `${THEMES}/10`)[0]?.body).toMatchObject({
      id: 10,
      isBuiltIn: false,
      name: 'Orange edited',
    });
    expect(toasts).toEqual(['Created Light edited', 'Saved Orange edited']);
  });

  it('Delete asks first, names the fallback for the active theme, and deletes on yes', async () => {
    const page = await mount();

    confirmAnswer = false;
    card(page, 10).querySelector<HTMLButtonElement>('[data-action="delete"]')!.click();
    await flush(page);
    expect(confirms[0]?.title).toBe('Delete Orange?');
    expect(confirms[0]?.message).toContain('falls back to the built-in Light theme');
    expect(api.calls('DELETE', `${THEMES}/10`)).toEqual([]);

    confirmAnswer = true;
    api.on('DELETE', `${THEMES}/10`, undefined);
    api.on('GET', THEMES, [light]);
    card(page, 10).querySelector<HTMLButtonElement>('[data-action="delete"]')!.click();
    await flush(page);
    expect(api.calls('DELETE', `${THEMES}/10`).length).toBe(1);
    expect(page.querySelectorAll('.themes__card').length).toBe(1);
    expect(toasts).toEqual(['Deleted Orange']);
  });
});
