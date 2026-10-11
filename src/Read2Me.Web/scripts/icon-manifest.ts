// The pure half of the icon pipeline (scripts/icons.ts): what the manifest must hold for a given
// IconName union, and the Google Fonts request that builds the matching subset font.

/** The manifest's content for a union: its names, sorted, each once. */
export const sortIconNames = (names: readonly string[]): string[] => [...new Set(names)].sort();

/** Why `manifest` is not the manifest of `union`, one line per problem; empty when it is. */
export function manifestProblems(union: readonly string[], manifest: unknown): string[] {
  if (!Array.isArray(manifest) || !manifest.every((name) => typeof name === 'string')) {
    return ['the manifest is not a JSON array of icon names'];
  }
  const names = manifest as string[];
  const expected = sortIconNames(union);
  const problems: string[] = [];
  const missing = expected.filter((name) => !names.includes(name));
  const extra = sortIconNames(names).filter((name) => !expected.includes(name));
  if (missing.length > 0) problems.push(`in IconName but not in the font: ${missing.join(', ')}`);
  if (extra.length > 0) problems.push(`in the font but not in IconName: ${extra.join(', ')}`);
  if (problems.length === 0 && names.join() !== expected.join()) {
    problems.push('the manifest is not sorted, or lists a name twice');
  }
  return problems;
}

/** The CSS2 request for a subset holding only `names`, with the full range of every axis. */
export function subsetCssUrl(names: readonly string[]): string {
  const sorted = sortIconNames(names);
  if (sorted.length === 0) throw new Error('no icon names to subset');
  const bad = sorted.filter((name) => !/^[a-z0-9_]+$/.test(name));
  if (bad.length > 0) throw new Error(`not Material Symbols names: ${bad.join(', ')}`);
  return (
    'https://fonts.googleapis.com/css2' +
    '?family=Material+Symbols+Rounded:opsz,wght,FILL,GRAD@20..48,100..700,0..1,-50..200' +
    `&icon_names=${sorted.join(',')}&display=block`
  );
}

/** The one woff2 the API's CSS points at. */
export function woff2Url(css: string): string {
  const urls = [...css.matchAll(/url\(([^)]+)\)\s*format\('woff2'\)/g)].map((m) => m[1]);
  const [url] = urls;
  if (url === undefined || urls.length !== 1) {
    throw new Error(`expected one woff2 in the Google Fonts CSS, found ${urls.length}`);
  }
  return url;
}
