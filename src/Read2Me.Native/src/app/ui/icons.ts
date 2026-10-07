/**
 * Every Material Symbols Rounded icon the app draws. The committed font is a subset holding only
 * these names, so after changing the list run `bun run icons` and commit the font and the manifest
 * (`bun run icons:check` fails until then).
 */
export const ICON_NAMES = [
  'auto_stories',
  'brightness_auto',
  'call_split',
  'check_circle',
  'compare',
  'construction',
  'dark_mode',
  'dashboard',
  'delete_forever',
  'description',
  'dns',
  'download',
  'equalizer',
  'error',
  'graphic_eq',
  'groups',
  'info',
  'library_books',
  'light_mode',
  'link',
  'menu',
  'menu_book',
  'palette',
  'person_add',
  'person_off',
  'person_search',
  'progress_activity',
  'psychology',
  'question_mark',
  'radio_button_unchecked',
  'rate_review',
  'record_voice_over',
  'search',
  'settings',
  'subtitles',
  'warning',
] as const;

export type IconName = (typeof ICON_NAMES)[number];
