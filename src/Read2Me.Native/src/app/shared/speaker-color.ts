/**
 * Deterministic per-character hue (design §7, `r2m-speaker-chip`).
 *
 * The same character id always lands on the same hue, in every session and on every screen, so a
 * producer learns "Hardin is teal" once. Saturation and lightness are fixed per colour scheme so the
 * chip stays legible on both light and dark surfaces; only the hue varies.
 */

export type ColorScheme = 'light' | 'dark';

const HUE_STEPS = 360;

/** FNV-1a 32-bit hash — small, stable, and spreads similar GUIDs across the hue circle. */
function fnv1a(text: string): number {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash >>> 0;
}

/** Hue in degrees, 0–359, for a character id (any non-empty string). */
export function speakerHue(id: string): number {
  return fnv1a(id.trim().toLowerCase()) % HUE_STEPS;
}

const SCHEME_PARAMS: Record<ColorScheme, { sat: number; light: number; bgLight: number }> = {
  light: { sat: 55, light: 36, bgLight: 93 },
  dark: { sat: 60, light: 74, bgLight: 20 },
};

export interface SpeakerColor {
  hue: number;
  /** Text / icon colour: `hsl(h s% l%)`. */
  foreground: string;
  /** Tinted chip background: `hsl(h s% l%)`. */
  background: string;
}

/** Concrete colours for a given scheme; the chip normally uses CSS `light-dark()` with the hue instead. */
export function speakerColor(id: string, scheme: ColorScheme): SpeakerColor {
  const hue = speakerHue(id);
  const p = SCHEME_PARAMS[scheme];
  return {
    hue,
    foreground: `hsl(${hue} ${p.sat}% ${p.light}%)`,
    background: `hsl(${hue} ${p.sat}% ${p.bgLight}%)`,
  };
}
