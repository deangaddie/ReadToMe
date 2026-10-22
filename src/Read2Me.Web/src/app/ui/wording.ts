/**
 * Noun-suffix pluralisation, the idiom the app's wording is written in:
 * `${n} item${plural(n)}`.
 */
export function plural(n: number): string {
  return n === 1 ? '' : 's';
}
