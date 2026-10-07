import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { AppTheme } from '@app/api';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../testing/fake-api';
import { ThemeService } from './theme.service';

const light: AppTheme = {
  id: 1,
  name: 'Light',
  isBuiltIn: true,
  isDark: false,
  primary: '#594AE2',
  secondary: '#FF4081',
};
const dark: AppTheme = { ...light, id: 2, name: 'Dark', isDark: true, primary: '#7C5CBF' };
const orange: AppTheme = {
  id: 10,
  name: 'Orange',
  isBuiltIn: false,
  isDark: true,
  primary: '#ff9900',
  secondary: '#00aaff',
};

interface FakeMediaQuery {
  matches: boolean;
  fire(matches: boolean): void;
}

function installMatchMedia(initialDark: boolean): FakeMediaQuery {
  let handler: ((e: { matches: boolean }) => void) | null = null;
  const fake: FakeMediaQuery = {
    matches: initialDark,
    fire(matches) {
      fake.matches = matches;
      handler?.({ matches });
    },
  };
  Object.defineProperty(globalThis, 'matchMedia', {
    configurable: true,
    writable: true,
    value: () => ({
      matches: fake.matches,
      addEventListener: (_: string, h: (e: { matches: boolean }) => void) => (handler = h),
      removeEventListener: () => undefined,
    }),
  });
  return fake;
}

const THEMES = '/api/settings/themes';
const SELECTION = '/api/settings/themes/selection';

describe('ThemeService', () => {
  let api: FakeApi;
  let media: FakeMediaQuery;

  beforeEach(() => {
    resetServices();
    media = installMatchMedia(false);
    document.getElementById('r2m-theme')?.remove();
    delete document.documentElement.dataset['theme'];
    api = new FakeApi();
    api.install();
  });

  afterEach(() => {
    document.getElementById('r2m-theme')?.remove();
    delete document.documentElement.dataset['theme'];
  });

  async function boot(selection: {
    selectedThemeId: number | null;
    followSystemPreference: boolean;
  }) {
    api.on('GET', THEMES, [light, dark, orange]);
    api.on('GET', SELECTION, selection);
    const service = new ThemeService();
    await service.load();
    return service;
  }

  function styleText(): string {
    return document.getElementById('r2m-theme')?.textContent ?? '';
  }

  it('applies the selected theme as data-theme + a stylesheet on boot', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: false });

    expect(service.effective()?.name).toBe('Orange');
    expect(document.documentElement.dataset['theme']).toBe('dark');
    expect(styleText()).toContain('--r2m-accent: #ff9900;');
    expect(service.preference()).toBe('dark');
  });

  it('follows the OS preference live without a reload', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: true });
    expect(service.effective()?.name).toBe('Light');
    expect(document.documentElement.dataset['theme']).toBe('light');
    expect(service.preference()).toBe('system');

    media.fire(true);

    expect(service.effective()?.name).toBe('Dark');
    expect(document.documentElement.dataset['theme']).toBe('dark');
    expect(styleText()).toContain('--r2m-accent: #7c5cbf;');
  });

  it('applyTheme writes the selection and turns follow-system off', async () => {
    const service = await boot({ selectedThemeId: null, followSystemPreference: true });
    api.on('PUT', SELECTION, (body) => body);

    await service.applyTheme(orange.id);

    expect(api.calls('PUT', SELECTION).map((r) => r.body)).toEqual([
      { selectedThemeId: orange.id, followSystemPreference: false },
    ]);
    expect(service.effective()?.name).toBe('Orange');
    expect(service.selectedId()).toBe(orange.id);
  });

  it('setPreference("light") selects the built-in Light row', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: false });
    api.on('PUT', SELECTION, (body) => body);

    await service.setPreference('light');

    expect(api.calls('PUT', SELECTION)[0]?.body).toEqual({
      selectedThemeId: light.id,
      followSystemPreference: false,
    });
    expect(document.documentElement.dataset['theme']).toBe('light');
  });

  it('setPreference("system") flips the follow flag', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: false });
    api.on('PUT', SELECTION, (body) => ({ selectedThemeId: orange.id, ...(body as object) }));

    await service.setPreference('system');

    expect(api.calls('PUT', SELECTION)[0]?.body).toEqual({ followSystemPreference: true });
    expect(service.preference()).toBe('system');
    expect(service.effective()?.name).toBe('Light');
  });

  it('keeps the compiled defaults and records the error when the host is unreachable', async () => {
    api.on('GET', THEMES, () => problem(503, 'down'));
    api.on('GET', SELECTION, { selectedThemeId: null, followSystemPreference: false });
    const service = new ThemeService();
    await service.load();

    expect(service.loaded()).toBe(true);
    expect(service.error()).toBeTruthy();
    expect(service.effective()).toBeNull();
    expect(document.getElementById('r2m-theme')).toBeNull();
    expect(document.documentElement.dataset['theme']).toBeUndefined();
  });

  it('delete clears the selection locally when the active theme is removed', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: false });
    api.on('DELETE', `${THEMES}/${orange.id}`, undefined);
    api.on('GET', THEMES, [light, dark]);

    await service.delete(orange.id);

    expect(api.calls('DELETE', `${THEMES}/${orange.id}`)).toHaveLength(1);
    expect(service.selectedId()).toBeNull();
    expect(service.effective()?.name).toBe('Light');
  });
});
