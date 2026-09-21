/** What every settings config editor checks and does the same way (tickets 21–22). */

const ABSOLUTE_URL = /^[a-z][a-z0-9+.-]*:\/\/\S+$/i;

export function isAbsoluteUrl(value: string): boolean {
  const trimmed = value.trim();
  return ABSOLUTE_URL.test(trimmed) && URL.canParse(trimmed);
}

/** "Name (copy)", then "(copy 2)", … — whichever no existing config already uses, ignoring case. */
export function duplicateName(name: string, existing: readonly string[]): string {
  const taken = new Set(existing.map((n) => n.trim().toLowerCase()));
  for (let n = 1; ; n++) {
    const candidate = n === 1 ? `${name} (copy)` : `${name} (copy ${n})`;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
}
