/** The app-relative cast route: the roster, or one character's detail. */
export function castPath(folder: string, characterId?: string | null): string {
  const base = `projects/${encodeURIComponent(folder)}/cast`;
  return characterId ? `${base}/${encodeURIComponent(characterId)}` : base;
}
