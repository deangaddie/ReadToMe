import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AppTheme } from '@app/api';
import { ThemeService } from '@app/theme/theme.service';
import { ThemesPage } from './themes-page';

const light: AppTheme = {
  id: 1,
  name: 'Light',
  isBuiltIn: true,
  isDark: false,
  primary: '#594AE2',
  secondary: '#FF4081',
};
const orange: AppTheme = {
  id: 10,
  name: 'Orange',
  isBuiltIn: false,
  isDark: true,
  primary: '#ff9900',
  secondary: '#00aaff',
  surface: '#222222',
};

describe('ThemesPage', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ThemesPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    const loading = TestBed.inject(ThemeService).load();
    http.expectOne('/api/settings/themes').flush([light, orange]);
    http
      .expectOne('/api/settings/themes/selection')
      .flush({ selectedThemeId: 10, followSystemPreference: false });
    await loading;
  });

  afterEach(() => http.verify());

  it('renders a card per theme with Active marker, lock and delete only where allowed', async () => {
    const fixture = TestBed.createComponent(ThemesPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const cards = el.querySelectorAll('.themes__card');
    expect(cards.length).toBe(2);
    const lightCard = el.querySelector('[data-theme-id="1"]')!;
    const orangeCard = el.querySelector('[data-theme-id="10"]')!;
    expect(lightCard.querySelector('.themes__lock')).not.toBeNull();
    expect(lightCard.querySelector('.themes__delete')).toBeNull();
    expect(orangeCard.querySelector('.themes__delete')).not.toBeNull();
    expect(orangeCard.textContent).toContain('Active');
    expect(orangeCard.classList.contains('themes__card--selected')).toBe(true);
    expect(orangeCard.querySelectorAll('.themes__swatch').length).toBe(3);
  });

  it('Apply writes the selection through the service', async () => {
    const fixture = TestBed.createComponent(ThemesPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    (el.querySelector('[data-theme-id="1"] .themes__actions button') as HTMLButtonElement).click();
    const req = http.expectOne('/api/settings/themes/selection');
    expect(req.request.body).toEqual({ selectedThemeId: 1, followSystemPreference: false });
    req.flush({ selectedThemeId: 1, followSystemPreference: false });
    await fixture.whenStable();

    expect(el.querySelector('[data-theme-id="1"]')?.textContent).toContain('Active');
    expect(document.documentElement.dataset['theme']).toBe('light');
  });

  it('follow-system toggle flips only the flag', async () => {
    const fixture = TestBed.createComponent(ThemesPage);
    await fixture.whenStable();

    const toggling = fixture.componentInstance.setFollowSystem(true);
    const req = http.expectOne('/api/settings/themes/selection');
    expect(req.request.body).toEqual({ followSystemPreference: true });
    req.flush({ selectedThemeId: 10, followSystemPreference: true });
    await toggling;
    await fixture.whenStable();

    expect(fixture.componentInstance.followSystem()).toBe(true);
  });
});
