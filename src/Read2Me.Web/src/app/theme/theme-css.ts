import { AppTheme, ThemeSelection } from '@app/api';

/**
 * AppTheme → CSS custom properties (design §3.2). Pure functions so the mapping table is unit
 * tested without a DOM. Angular Material 22 reads `--mat-sys-*`; the app's own surfaces read
 * `--r2m-*` (tokens.scss). Status colours are deliberately absent: themes never touch them.
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
 * Variables that depend only on the theme's accent colours, not on its surfaces. These are also
 * applied inside the style guide's pinned `[data-scheme]` panels so a custom primary is previewed
 * in both schemes; containers mix against whatever `--mat-sys-surface` is in scope.
 */
export function accentCssVars(theme: AppTheme): Record<string, string> {
  const primary = normalizeHex(theme.primary);
  const secondary = normalizeHex(theme.secondary);
  const dark = theme.isDark;
  return {
    '--mat-sys-primary': primary,
    '--mat-sys-on-primary': contrastText(primary),
    '--mat-sys-primary-container': mix(primary, dark ? 32 : 20, 'var(--mat-sys-surface)'),
    '--mat-sys-on-primary-container': dark
      ? mix(primary, 45, '#ffffff')
      : mix(primary, 65, '#000000'),
    '--mat-sys-surface-tint': primary,
    '--mat-sys-secondary': secondary,
    '--mat-sys-on-secondary': contrastText(secondary),
    '--mat-sys-secondary-container': mix(secondary, dark ? 32 : 20, 'var(--mat-sys-surface)'),
    '--mat-sys-on-secondary-container': dark
      ? mix(secondary, 45, '#ffffff')
      : mix(secondary, 65, '#000000'),
    '--r2m-accent': primary,
  };
}

/** The full variable set for the effective theme, applied on `:root`. */
export function themeCssVars(theme: AppTheme): Record<string, string> {
  const vars = accentCssVars(theme);

  if (has(theme.background)) {
    const background = normalizeHex(theme.background);
    vars['--mat-sys-background'] = background;
    vars['--mat-sys-surface'] = background;
    vars['--mat-sys-surface-container-lowest'] = background;
  }
  if (has(theme.surface)) {
    const surface = normalizeHex(theme.surface);
    vars['--mat-sys-surface-container'] = surface;
    vars['--mat-sys-surface-container-low'] = mix(surface, 50, 'var(--mat-sys-surface)');
    vars['--mat-sys-surface-container-high'] = mix(surface, 90, 'var(--mat-sys-on-surface)');
    vars['--mat-sys-surface-container-highest'] = mix(surface, 82, 'var(--mat-sys-on-surface)');
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
    vars['--r2m-nav-active-bg'] = mix(vars['--mat-sys-primary'] ?? theme.primary, 28, drawer);
    vars['--r2m-nav-active-fg'] = fg;
  }
  if (has(theme.textPrimary)) {
    const text = normalizeHex(theme.textPrimary);
    vars['--mat-sys-on-surface'] = text;
    vars['--mat-sys-on-background'] = text;
  }
  if (has(theme.textSecondary)) {
    vars['--mat-sys-on-surface-variant'] = normalizeHex(theme.textSecondary);
  }
  return vars;
}

/** Stylesheet text injected as `<style id="r2m-theme">`; `[data-scheme]` panels get the accent set. */
export function buildThemeStylesheet(theme: AppTheme): string {
  const block = (selector: string, vars: Record<string, string>) =>
    `${selector} {\n${Object.entries(vars)
      .map(([k, v]) => `  ${k}: ${v};`)
      .join('\n')}\n}`;
  return [
    `/* ${theme.name} (${theme.isDark ? 'dark' : 'light'}) */`,
    block(':root', themeCssVars(theme)),
    block("[data-scheme='light'], [data-scheme='dark']", accentCssVars(theme)),
  ].join('\n');
}

/**
 * Which theme is in force (design §3.4): follow-system picks the built-in Light/Dark row by the OS
 * preference; otherwise the selected row; otherwise the first built-in light theme.
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
