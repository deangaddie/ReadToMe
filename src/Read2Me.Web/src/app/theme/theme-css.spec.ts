import { describe, expect, it } from 'bun:test';
import type { AppTheme } from '@app/api';
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

describe('themeCssVars (spec §5.1 token mapping)', () => {
  it('maps Primary and Secondary to the accent and secondary tokens', () => {
    const vars = themeCssVars(light);
    expect(vars['--r2m-accent']).toBe('#594ae2');
    expect(vars['--r2m-on-accent']).toBe('#ffffff');
    expect(vars['--r2m-accent-container']).toContain('color-mix(in srgb, #594ae2 20%');
    expect(vars['--r2m-secondary']).toBe('#ff4081');
    expect(vars['--r2m-on-secondary']).toBe('#ffffff');
  });

  it('writes only --r2m-* variables, never Material system tokens', () => {
    for (const theme of [light, sunset, custom]) {
      expect(Object.keys(themeCssVars(theme)).every((k) => k.startsWith('--r2m-'))).toBe(true);
    }
  });

  it('omits surface tokens when the theme leaves them to the compiled palette', () => {
    const vars = themeCssVars(light);
    expect(vars['--r2m-surface']).toBeUndefined();
    expect(vars['--r2m-surface-container']).toBeUndefined();
    expect(vars['--r2m-appbar-bg']).toBeUndefined();
    expect(vars['--r2m-text']).toBeUndefined();
  });

  it('maps every optional colour when present', () => {
    const vars = themeCssVars(sunset);
    expect(vars['--r2m-surface']).toBe('#fff5f2');
    expect(vars['--r2m-surface-container']).toBe('#ffffff');
    expect(vars['--r2m-surface-low']).toContain('#ffffff 50%');
    expect(vars['--r2m-surface-high']).toContain('#ffffff 90%');
    expect(vars['--r2m-surface-highest']).toContain('#ffffff 82%');
    expect(vars['--r2m-appbar-bg']).toBe('#ff6f61');
    expect(vars['--r2m-appbar-fg']).toBe('#ffffff'); // coral is below the luminance cut-off
    expect(vars['--r2m-text']).toBe('#4a4a4a');
  });

  it('derives nav colours from the drawer background and honours text-secondary', () => {
    const vars = themeCssVars(custom);
    expect(vars['--r2m-nav-bg']).toBe('#101010');
    expect(vars['--r2m-nav-active-fg']).toBe('#ffffff');
    expect(vars['--r2m-nav-active-bg']).toContain('#ff9900 28%');
    expect(vars['--r2m-text-muted']).toBe('#bbbbbb');
    expect(vars['--r2m-accent-container']).toContain('#ff9900 32%'); // dark: stronger tint
  });

  it('never emits status tokens', () => {
    expect(Object.keys(themeCssVars(sunset)).some((k) => k.startsWith('--r2m-status'))).toBe(false);
  });
});

describe('buildThemeStylesheet', () => {
  it('emits one :root block and no pinned-scheme block', () => {
    const css = buildThemeStylesheet(sunset);
    expect(css).toContain('/* Sunset (light) */');
    expect(css).toContain(':root {');
    expect(css).toContain('--r2m-appbar-bg: #ff6f61;');
    expect(css).not.toContain('data-scheme');
    expect(css.match(/\{/g)).toHaveLength(1);
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
