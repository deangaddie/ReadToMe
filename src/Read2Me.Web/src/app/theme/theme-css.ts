import type { AppTheme, ThemeSelection } from '@app/api';

/**
 * AppTheme → CSS custom properties (spec §5.1). Pure functions so the mapping table is unit tested
 * without a DOM. Every variable is a `--r2m-*` token from `global.css`, whose `light-dark()` values
 * are the compiled fallback a theme overrides. Status colours are deliberately absent: themes
 * never touch them.
 */

export type Scheme = 'light' | 'dark';

/** `#rgb` or `#rrggbb` → `#rrggbb` lower-case; anything else returned as-is. */
export function normalizeHex(value: string): string {
  const v = value.trim();
  const short = /^#([0-9a-f])([0-9a-f])([0-9a-f])$/i.exec(v);
  if (short)
    return `#${short[1]}${short[1]}${short[2]}${short[2]}${short[3]}${short[3]}`.toLowerCase();
  return /^#[0-9a-f]{6}$/i.test(v) ? v.toLowerCase() : v;
}

export function isHexColour(value: string | null | undefined): value is string {
  return typeof value === 'string' && /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(value.trim());
}

/** WCAG relative luminance of a hex colour, 0 (black) to 1 (white). */
export function luminance(hex: string): number {
  const h = normalizeHex(hex).slice(1);
  const channel = (i: number) => {
    const c = parseInt(h.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(0) + 0.7152 * channel(2) + 0.0722 * channel(4);
}

/** Black or white, whichever contrasts better with `hex`. */
export function contrastText(hex: string): '#000000' | '#ffffff' {
  return luminance(hex) > 0.36 ? '#000000' : '#ffffff';
}

function mix(colour: string, pct: number, withColour: string): string {
  return `color-mix(in srgb, ${colour} ${pct}%, ${withColour})`;
}

function has(value: string | null | undefined): value is string {
  return isHexColour(value);
}

/**
 * Variables that depend only on the theme's accent colours, not on its surfaces; the containers
 * mix against whatever `--r2m-surface` is in force.
 */
export function accentCssVars(theme: AppTheme): Record<string, string> {
  const primary = normalizeHex(theme.primary);
  const secondary = normalizeHex(theme.secondary);
  const dark = theme.isDark;
  return {
    '--r2m-accent': primary,
    '--r2m-on-accent': contrastText(primary),
    '--r2m-accent-container': mix(primary, dark ? 32 : 20, 'var(--r2m-surface)'),
    '--r2m-on-accent-container': dark ? mix(primary, 45, '#ffffff') : mix(primary, 65, '#000000'),
    '--r2m-secondary': secondary,
    '--r2m-on-secondary': contrastText(secondary),
    '--r2m-secondary-container': mix(secondary, dark ? 32 : 20, 'var(--r2m-surface)'),
    '--r2m-on-secondary-container': dark
      ? mix(secondary, 45, '#ffffff')
      : mix(secondary, 65, '#000000'),
  };
}

/** The full variable set for the effective theme, applied on `:root`. */
export function themeCssVars(theme: AppTheme): Record<string, string> {
  const vars = accentCssVars(theme);

  if (has(theme.background)) {
    vars['--r2m-surface'] = normalizeHex(theme.background);
  }
  if (has(theme.surface)) {
    const surface = normalizeHex(theme.surface);
    vars['--r2m-surface-container'] = surface;
    vars['--r2m-surface-low'] = mix(surface, 50, 'var(--r2m-surface)');
    vars['--r2m-surface-high'] = mix(surface, 90, 'var(--r2m-text)');
    vars['--r2m-surface-highest'] = mix(surface, 82, 'var(--r2m-text)');
  }
  if (has(theme.appbarBackground)) {
    const appbar = normalizeHex(theme.appbarBackground);
    vars['--r2m-appbar-bg'] = appbar;
    vars['--r2m-appbar-fg'] = contrastText(appbar);
  }
  if (has(theme.drawerBackground)) {
    const drawer = normalizeHex(theme.drawerBackground);
    const fg = contrastText(drawer);
    vars['--r2m-nav-bg'] = drawer;
    vars['--r2m-nav-fg'] = mix(fg, 80, drawer);
    vars['--r2m-nav-active-bg'] = mix(vars['--r2m-accent'] ?? theme.primary, 28, drawer);
    vars['--r2m-nav-active-fg'] = fg;
  }
  if (has(theme.textPrimary)) {
    vars['--r2m-text'] = normalizeHex(theme.textPrimary);
  }
  if (has(theme.textSecondary)) {
    vars['--r2m-text-muted'] = normalizeHex(theme.textSecondary);
  }
  return vars;
}

/** Stylesheet text injected as `<style id="r2m-theme">`: one `:root` block. */
export function buildThemeStylesheet(theme: AppTheme): string {
  const body = Object.entries(themeCssVars(theme))
    .map(([k, v]) => `  ${k}: ${v};`)
    .join('\n');
  return `/* ${theme.name} (${theme.isDark ? 'dark' : 'light'}) */\n:root {\n${body}\n}`;
}

/**
 * Which theme is in force: follow-system picks the built-in Light/Dark row by the OS preference;
 * otherwise the selected row; otherwise the first built-in light theme.
 */
export function resolveEffectiveTheme(
  themes: readonly AppTheme[],
  selection: ThemeSelection | null,
  prefersDark: boolean,
): AppTheme | null {
  if (themes.length === 0) return null;
  const builtIn = themes.filter((t) => t.isBuiltIn);
  const fallbackLight =
    builtIn.find((t) => t.name === 'Light') ??
    builtIn.find((t) => !t.isDark) ??
    themes.find((t) => !t.isDark) ??
    themes[0] ??
    null;

  if (selection?.followSystemPreference) {
    if (!prefersDark) return fallbackLight;
    return (
      builtIn.find((t) => t.name === 'Dark') ??
      builtIn.find((t) => t.isDark) ??
      themes.find((t) => t.isDark) ??
      fallbackLight
    );
  }
  if (selection?.selectedThemeId != null) {
    return themes.find((t) => t.id === selection.selectedThemeId) ?? fallbackLight;
  }
  return fallbackLight;
}
