import { CharacterDto, NarratorDto } from '@app/api';
import { SpeakerRosterEntry } from '@app/ui/speaker-menu/speaker-menu';

/**
 * The roster the speaker menu offers (ticket 12, research §3 "Speaker chip menu"): the narrator
 * first — "Narrator (Alice)" when linked, since narration is then spoken as Alice — then every
 * character with their aliases for search. The narrator's own row is dropped from the character
 * list so a linked narrator appears once, under the narrator label, and picking it stamps the
 * same id either way. The menu itself sorts (narrator first, then by name).
 */
export function buildRoster(
  characters: readonly CharacterDto[],
  narrator: NarratorDto | null,
): SpeakerRosterEntry[] {
  const roster: SpeakerRosterEntry[] = [];
  if (narrator) {
    roster.push({
      id: narrator.characterId,
      name: narrator.isLinked ? `Narrator (${narrator.displayName})` : 'Narrator',
      isNarrator: true,
    });
  }
  for (const c of characters) {
    if (narrator && c.id === narrator.characterId) continue;
    roster.push({ id: c.id, name: c.name, aliases: c.aliases.map((a) => a.name) });
  }
  return roster;
}
