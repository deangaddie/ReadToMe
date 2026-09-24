import { CharacterDto } from '@app/api';
import { buildRoster } from './speaker-roster';

const NARRATOR_ID = '00000000-0000-0000-0000-000000000001';

const CHARACTERS: CharacterDto[] = [
  { id: 'h', name: 'Hardin', aliases: [{ id: 'a1', name: 'Salvor' }] },
  { id: NARRATOR_ID, name: 'Narrator', aliases: [] },
  { id: 'p', name: 'Pirenne', aliases: [] },
];

describe('buildRoster', () => {
  it('puts the narrator first and drops the seed narrator row from the characters', () => {
    const roster = buildRoster(CHARACTERS, {
      characterId: NARRATOR_ID,
      displayName: 'Narrator',
      isLinked: false,
    });
    expect(roster).toEqual([
      { id: NARRATOR_ID, name: 'Narrator', isNarrator: true },
      { id: 'h', name: 'Hardin', aliases: ['Salvor'] },
      { id: 'p', name: 'Pirenne', aliases: [] },
    ]);
  });

  it('a linked narrator is labelled with the character and listed once', () => {
    const roster = buildRoster(CHARACTERS, { characterId: 'h', displayName: 'Hardin', isLinked: true });
    expect(roster.map((e) => e.name)).toEqual(['Narrator (Hardin)', 'Narrator', 'Pirenne']);
    expect(roster[0]).toEqual({ id: 'h', name: 'Narrator (Hardin)', isNarrator: true });
    expect(new Set(roster.map((e) => e.id)).size).toBe(roster.length);
  });

  it('with no narrator known, lists the characters as they are', () => {
    expect(buildRoster(CHARACTERS, null).map((e) => e.id)).toEqual(['h', NARRATOR_ID, 'p']);
  });
});
