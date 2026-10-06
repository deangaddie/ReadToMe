/**
 * Every Material Symbols Rounded icon the app draws. The committed font is a subset holding only
 * these names, so after changing the list run `bun run icons` and commit the font and the manifest
 * (`bun run icons:check` fails until then).
 */
export const ICON_NAMES = [
  'auto_stories',
  'call_split',
  'check_circle',
  'construction',
  'delete_forever',
  'error',
  'graphic_eq',
  'info',
  'link',
  'person_add',
  'person_off',
  'person_search',
  'progress_activity',
  'question_mark',
  'radio_button_unchecked',
  'rate_review',
  'search',
  'warning',
] as const;

export type IconName = (typeof ICON_NAMES)[number];
