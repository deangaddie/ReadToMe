/** The app-relative cast route: the roster, or one character's detail. */
export function castPath(folder: string, characterId?: string | null): string {
  const base = `projects/${encodeURIComponent(folder)}/cast`;
  return characterId ? `${base}/${encodeURIComponent(characterId)}` : base;
}

/**
 * The series cast board with a Series character's inline Voice editor open (#221): where a linked
 * Character's shared Voices are edited. The Projects page reads `series` and `character`.
 */
export function seriesBoardPath(seriesId: string, seriesCharacterId: string): string {
  return `projects?series=${encodeURIComponent(seriesId)}&character=${encodeURIComponent(seriesCharacterId)}`;
}
