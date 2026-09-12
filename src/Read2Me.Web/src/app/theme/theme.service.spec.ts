import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AppTheme } from '@app/api';
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
  Object.defineProperty(window, 'matchMedia', {
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

describe('ThemeService', () => {
  let http: HttpTestingController;
  let media: FakeMediaQuery;

  beforeEach(() => {
    media = installMatchMedia(false);
    document.getElementById('r2m-theme')?.remove();
    delete document.documentElement.dataset['theme'];
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function boot(selection: {
    selectedThemeId: number | null;
    followSystemPreference: boolean;
  }) {
    const service = TestBed.inject(ThemeService);
    const loading = service.load();
    http.expectOne('/api/settings/themes').flush([light, dark, orange]);
    http.expectOne('/api/settings/themes/selection').flush(selection);
    await loading;
    TestBed.tick();
    return service;
  }

  function styleText(): string {
    return document.getElementById('r2m-theme')?.textContent ?? '';
  }

  it('applies the selected theme as data-theme + a stylesheet on boot', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: false });

    expect(service.effective()?.name).toBe('Orange');
    expect(document.documentElement.dataset['theme']).toBe('dark');
    expect(styleText()).toContain('--mat-sys-primary: #ff9900;');
    expect(service.preference()).toBe('dark');
  });

  it('follows the OS preference live without a reload', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: true });
    expect(service.effective()?.name).toBe('Light');
    expect(document.documentElement.dataset['theme']).toBe('light');
    expect(service.preference()).toBe('system');

    media.fire(true);
    TestBed.tick();

    expect(service.effective()?.name).toBe('Dark');
    expect(document.documentElement.dataset['theme']).toBe('dark');
    expect(styleText()).toContain('--mat-sys-primary: #7c5cbf;');
  });

  it('applyTheme writes the selection and turns follow-system off', async () => {
    const service = await boot({ selectedThemeId: null, followSystemPreference: true });

    const applying = service.applyTheme(orange.id);
    const req = http.expectOne('/api/settings/themes/selection');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ selectedThemeId: orange.id, followSystemPreference: false });
    req.flush({ selectedThemeId: orange.id, followSystemPreference: false });
    await applying;
    TestBed.tick();

    expect(service.effective()?.name).toBe('Orange');
    expect(service.selectedId()).toBe(orange.id);
  });

  it('setPreference("light") selects the built-in Light row', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: false });

    const setting = service.setPreference('light');
    const req = http.expectOne('/api/settings/themes/selection');
    expect(req.request.body).toEqual({ selectedThemeId: light.id, followSystemPreference: false });
    req.flush({ selectedThemeId: light.id, followSystemPreference: false });
    await setting;
    TestBed.tick();
    expect(document.documentElement.dataset['theme']).toBe('light');
  });

  it('keeps the compiled defaults and records the error when the host is unreachable', async () => {
    const service = TestBed.inject(ThemeService);
    const loading = service.load();
    http.expectOne('/api/settings/themes').error(new ProgressEvent('error'), { status: 0 });
    http
      .expectOne('/api/settings/themes/selection')
      .flush({ selectedThemeId: null, followSystemPreference: false });
    await loading;

    expect(service.loaded()).toBe(true);
    expect(service.error()).toBeTruthy();
    expect(service.effective()).toBeNull();
    expect(document.getElementById('r2m-theme')).toBeNull();
  });

  it('delete clears the selection locally when the active theme is removed', async () => {
    const service = await boot({ selectedThemeId: orange.id, followSystemPreference: false });

    const deleting = service.delete(orange.id);
    http
      .expectOne(`/api/settings/themes/${orange.id}`)
      .flush(null, { status: 204, statusText: 'No Content' });
    await new Promise((resolve) => setTimeout(resolve)); // let the delete continuation issue the reload
    http.expectOne('/api/settings/themes').flush([light, dark]);
    await deleting;
    TestBed.tick();

    expect(service.selectedId()).toBeNull();
    expect(service.effective()?.name).toBe('Light');
  });
});
