import { AppTheme } from '@app/api';
import {
  buildThemeStylesheet,
  contrastText,
  normalizeHex,
  resolveEffectiveTheme,
  themeCssVars,
} from './theme-css';

const light: AppTheme = {
  id: 1,
  name: 'Light',
  isBuiltIn: true,
  isDark: false,
  primary: '#594AE2',
  secondary: '#FF4081',
};
const dark: AppTheme = { ...light, id: 2, name: 'Dark', isDark: true, primary: '#7C5CBF' };
const sunset: AppTheme = {
  id: 5,
  name: 'Sunset',
  isBuiltIn: true,
  isDark: false,
  primary: '#FF6F61',
  secondary: '#FFB347',
  background: '#FFF5F2',
  surface: '#FFFFFF',
  appbarBackground: '#FF6F61',
  textPrimary: '#4A4A4A',
};
const custom: AppTheme = {
  id: 10,
  name: 'Orange',
  isBuiltIn: false,
  isDark: true,
  primary: '#ff9900',
  secondary: '#0af',
  drawerBackground: '#101010',
  textSecondary: '#bbb',
};

describe('theme-css helpers', () => {
  it('normalises short and long hex', () => {
    expect(normalizeHex('#ABC')).toBe('#aabbcc');
    expect(normalizeHex(' #FF9900 ')).toBe('#ff9900');
    expect(normalizeHex('orange')).toBe('orange');
  });

  it('picks black text on light colours and white on dark ones', () => {
    expect(contrastText('#ff9900')).toBe('#000000');
    expect(contrastText('#594AE2')).toBe('#ffffff');
    expect(contrastText('#FFFFFF')).toBe('#000000');
    expect(contrastText('#010101')).toBe('#ffffff');
  });
});

describe('themeCssVars (design §3.2 mapping table)', () => {
  it('maps Primary and Secondary to the Material system tokens plus the accent', () => {
    const vars = themeCssVars(light);
    expect(vars['--mat-sys-primary']).toBe('#594ae2');
    expect(vars['--mat-sys-on-primary']).toBe('#ffffff');
    expect(vars['--mat-sys-primary-container']).toContain('color-mix(in srgb, #594ae2 20%');
    expect(vars['--mat-sys-secondary']).toBe('#ff4081');
    expect(vars['--r2m-accent']).toBe('#594ae2');
  });

  it('omits surface tokens when the theme leaves them to the compiled palette', () => {
    const vars = themeCssVars(light);
    expect(vars['--mat-sys-background']).toBeUndefined();
    expect(vars['--mat-sys-surface-container']).toBeUndefined();
    expect(vars['--r2m-appbar-bg']).toBeUndefined();
    expect(vars['--mat-sys-on-surface']).toBeUndefined();
  });

  it('maps every optional colour when present', () => {
    const vars = themeCssVars(sunset);
    expect(vars['--mat-sys-background']).toBe('#fff5f2');
    expect(vars['--mat-sys-surface']).toBe('#fff5f2');
    expect(vars['--mat-sys-surface-container']).toBe('#ffffff');
    expect(vars['--mat-sys-surface-container-low']).toContain('#ffffff 50%');
    expect(vars['--r2m-appbar-bg']).toBe('#ff6f61');
    expect(vars['--r2m-appbar-fg']).toBe('#ffffff'); // coral is below the luminance cut-off
    expect(vars['--mat-sys-on-surface']).toBe('#4a4a4a');
    expect(vars['--mat-sys-on-background']).toBe('#4a4a4a');
  });

  it('derives nav colours from the drawer background and honours text-secondary', () => {
    const vars = themeCssVars(custom);
    expect(vars['--r2m-nav-bg']).toBe('#101010');
    expect(vars['--r2m-nav-active-fg']).toBe('#ffffff');
    expect(vars['--mat-sys-on-surface-variant']).toBe('#bbbbbb');
    expect(vars['--mat-sys-primary-container']).toContain('#ff9900 32%'); // dark: stronger tint
  });

  it('never emits status tokens', () => {
    expect(Object.keys(themeCssVars(sunset)).some((k) => k.startsWith('--r2m-status'))).toBe(false);
  });
});

describe('buildThemeStylesheet', () => {
  it('emits a :root block and an accent-only block for pinned-scheme panels', () => {
    const css = buildThemeStylesheet(sunset);
    expect(css).toContain(':root {');
    expect(css).toContain('--r2m-appbar-bg: #ff6f61;');
    const panels = css.slice(css.indexOf("[data-scheme='light'], [data-scheme='dark'] {"));
    expect(panels).toContain('--mat-sys-primary: #ff6f61;');
    expect(panels).not.toContain('--r2m-appbar-bg');
    expect(panels).not.toContain('--mat-sys-background');
  });
});

describe('resolveEffectiveTheme', () => {
  const themes = [light, dark, sunset, custom];

  it('follows the OS preference to the built-in Light / Dark rows', () => {
    const follow = { selectedThemeId: custom.id, followSystemPreference: true };
    expect(resolveEffectiveTheme(themes, follow, true)?.name).toBe('Dark');
    expect(resolveEffectiveTheme(themes, follow, false)?.name).toBe('Light');
  });

  it('uses the selected theme otherwise', () => {
    expect(
      resolveEffectiveTheme(themes, { selectedThemeId: 10, followSystemPreference: false }, true)
        ?.name,
    ).toBe('Orange');
  });

  it('falls back to the built-in light theme when nothing is selected or the id is gone', () => {
    expect(resolveEffectiveTheme(themes, null, true)?.name).toBe('Light');
    expect(
      resolveEffectiveTheme(themes, { selectedThemeId: 999, followSystemPreference: false }, false)
        ?.name,
    ).toBe('Light');
    expect(resolveEffectiveTheme([], null, false)).toBeNull();
  });
});
