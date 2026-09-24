import { AppTheme } from '@app/api';
import { isHexColour, normalizeHex } from '@app/theme/theme-css';

/** Editable copy of a theme; optional colours are '' while blank so inputs stay controlled. */
export interface ThemeDraft {
  name: string;
  isDark: boolean;
  primary: string;
  secondary: string;
  background: string;
  surface: string;
  appbarBackground: string;
  drawerBackground: string;
  textPrimary: string;
  textSecondary: string;
}

export type OptionalColourKey = Exclude<
  keyof ThemeDraft,
  'name' | 'isDark' | 'primary' | 'secondary'
>;

export const OPTIONAL_COLOURS: { key: OptionalColourKey; label: string; hint: string }[] = [
  { key: 'background', label: 'Background', hint: 'Page background' },
  { key: 'surface', label: 'Surface', hint: 'Cards, drawers, menus' },
  { key: 'appbarBackground', label: 'App bar', hint: 'Top bar background' },
  { key: 'drawerBackground', label: 'Nav rail', hint: 'Navigation rail background' },
  { key: 'textPrimary', label: 'Text', hint: 'Primary text colour' },
  { key: 'textSecondary', label: 'Secondary text', hint: 'Muted labels and hints' },
];

export const DEFAULT_DRAFT: ThemeDraft = {
  name: '',
  isDark: false,
  primary: '#594ae2',
  secondary: '#ff4081',
  background: '',
  surface: '',
  appbarBackground: '',
  drawerBackground: '',
  textPrimary: '',
  textSecondary: '',
};

export function toDraft(theme: AppTheme): ThemeDraft {
  return {
    name: theme.name,
    isDark: theme.isDark,
    primary: normalizeHex(theme.primary),
    secondary: normalizeHex(theme.secondary),
    background: theme.background ? normalizeHex(theme.background) : '',
    surface: theme.surface ? normalizeHex(theme.surface) : '',
    appbarBackground: theme.appbarBackground ? normalizeHex(theme.appbarBackground) : '',
    drawerBackground: theme.drawerBackground ? normalizeHex(theme.drawerBackground) : '',
    textPrimary: theme.textPrimary ? normalizeHex(theme.textPrimary) : '',
    textSecondary: theme.textSecondary ? normalizeHex(theme.textSecondary) : '',
  };
}

/** Draft → API body; blank optionals become null so the host stores "use palette default". */
export function fromDraft(draft: ThemeDraft): Omit<AppTheme, 'id' | 'isBuiltIn'> {
  const optional = (v: string) => (v.trim() ? normalizeHex(v) : null);
  return {
    name: draft.name.trim(),
    isDark: draft.isDark,
    primary: normalizeHex(draft.primary),
    secondary: normalizeHex(draft.secondary),
    background: optional(draft.background),
    surface: optional(draft.surface),
    appbarBackground: optional(draft.appbarBackground),
    drawerBackground: optional(draft.drawerBackground),
    textPrimary: optional(draft.textPrimary),
    textSecondary: optional(draft.textSecondary),
  };
}

export type ThemeDraftErrors = Partial<Record<keyof ThemeDraft, string>>;

/** Same rules as the host's validation, so a valid form never round-trips a 400. */
export function validateDraft(draft: ThemeDraft): ThemeDraftErrors {
  const errors: ThemeDraftErrors = {};
  if (!draft.name.trim()) errors.name = 'Name is required';
  if (!isHexColour(draft.primary)) errors.primary = 'Use #rgb or #rrggbb';
  if (!isHexColour(draft.secondary)) errors.secondary = 'Use #rgb or #rrggbb';
  for (const { key } of OPTIONAL_COLOURS) {
    const value = draft[key];
    if (value.trim() && !isHexColour(value)) errors[key] = 'Use #rgb or #rrggbb, or clear';
  }
  return errors;
}

export function isDraftValid(draft: ThemeDraft): boolean {
  return Object.keys(validateDraft(draft)).length === 0;
}
