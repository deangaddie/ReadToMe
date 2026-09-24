/**
 * Line-for-line port of `Read2Me.Core.Models.AliasCollisions`, tested against the same fixtures
 * (`alias-collisions.spec.ts` ⇔ `AliasCollisionsTests.cs`). The host answers the initial set with
 * the discovery outcome; the review dialog recomputes it as the user edits rows.
 *
 * Attribution resolves a speaker string to one character by first match over the roster, so a
 * string two characters own silently binds to whichever sorts first. Advisory: nothing here blocks
 * an apply.
 */

/** A discovery row about to be applied; `existingCharacterId` names the roster character it folds into. */
export interface AliasClaim {
  name: string;
  aliases: readonly string[];
  existingCharacterId?: string | null;
}

/** A roster character: id, primary name and aliases. */
export interface AliasOwner {
  id: string;
  name: string;
  aliases: readonly string[];
}

/**
 * The strings owned by two or more characters, in first-seen spelling; compare with
 * {@link collides}. Pass only the rows that will be applied.
 */
export function findAliasCollisions(
  rows: readonly AliasClaim[],
  roster: readonly AliasOwner[],
): string[] {
  const claimed = new Set(
    rows.map((r) => r.existingCharacterId).filter((id): id is string => !!id),
  );
  const owners: (readonly string[])[] = [
    ...rows.map((r) => [r.name, ...r.aliases]),
    ...roster.filter((c) => !claimed.has(c.id)).map((c) => [c.name, ...c.aliases]),
  ];

  const seen = new Set<string>();
  const collisions = new Map<string, string>();
  for (const owner of owners) {
    // Distinct within an owner: one character listing the same alias twice is untidy, not ambiguous.
    const names = new Map<string, string>();
    for (const raw of owner) {
      const name = raw.trim();
      if (name.length === 0) continue;
      const key = fold(name);
      if (!names.has(key)) names.set(key, name);
    }
    for (const [key, name] of names) {
      if (seen.has(key)) {
        if (!collisions.has(key)) collisions.set(key, name);
      } else {
        seen.add(key);
      }
    }
  }
  return [...collisions.values()];
}

/** Whether `name` is one of the collisions, case- and whitespace-insensitively. */
export function collides(collisions: readonly string[], name: string): boolean {
  const key = fold(name.trim());
  return collisions.some((c) => fold(c) === key);
}

function fold(name: string): string {
  return name.toLowerCase();
}
