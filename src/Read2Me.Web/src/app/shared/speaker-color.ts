/**
 * Deterministic per-character hue (design §7, `r2m-speaker-chip`).
 *
 * The same character id always lands on the same hue, in every session and on every screen, so a
 * producer learns "Elizabeth is teal" once. Saturation and lightness are fixed per colour scheme so the
 * chip stays legible on both light and dark surfaces; only the hue varies.
 */

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
