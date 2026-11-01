import type { AppTheme } from '@app/api';

/** A theme row's optional colours, all on the palette default: the host sends each, null when unset. */
export const PALETTE_DEFAULTS = {
  background: null,
  surface: null,
  appbarBackground: null,
  drawerBackground: null,
  textPrimary: null,
  textSecondary: null,
} satisfies Partial<AppTheme>;
