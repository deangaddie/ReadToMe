/**
 * The hub compares project folders case-insensitively; so does every match in the web app. This is
 * the one place that rule is written.
 */
export function sameFolder(a: string, b: string): boolean {
  return folderKey(a) === folderKey(b);
}

/** The key a map indexed by folder uses, so two spellings of one folder share an entry. */
export function folderKey(folder: string): string {
  return folder.toLowerCase();
}

/** The key of `byFolder` that spells `folder`, in any case; undefined when none. */
export function keyFor(byFolder: Record<string, unknown> | null | undefined, folder: string) {
  return Object.keys(byFolder ?? {}).find((k) => sameFolder(k, folder));
}

/** The entry of `byFolder` whose key is `folder` in any spelling; undefined when none. */
export function entryFor<T>(
  byFolder: Record<string, T> | null | undefined,
  folder: string,
): T | undefined {
  const key = keyFor(byFolder, folder);
  return key === undefined ? undefined : byFolder?.[key];
}
