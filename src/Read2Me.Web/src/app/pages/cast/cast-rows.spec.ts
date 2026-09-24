import { CharacterSummaryDto } from '@app/api';
import { displayName, filterRows, hasBookIcon, readinessChip, sortRows } from './cast-rows';

function row(overrides: Partial<CharacterSummaryDto> & { name: string }): CharacterSummaryDto {
  return {
    id: overrides.name.toLowerCase(),
    aliases: [],
    lineCount: 0,
    voiceCount: 0,
    readyVoiceCount: 0,
    isNarrator: false,
    narratesBook: false,
    ...overrides,
  };
}

describe('readinessChip', () => {
  it('is absent without voices', () => {
    expect(readinessChip(0, 0)).toBeNull();
  });

  it('is an error when none is ready, showing ready / total', () => {
    expect(readinessChip(0, 2)).toEqual({
      status: 'error',
      label: '0 / 2',
      tooltip: '0 of 2 voices ready for TTS',
    });
  });

  it('warns when some are ready', () => {
    expect(readinessChip(1, 3)).toMatchObject({ status: 'warn', label: '1 / 3' });
  });

  it('is info with only the total when all are ready', () => {
    expect(readinessChip(1, 1)).toEqual({
      status: 'info',
      label: '1',
      tooltip: '1 of 1 voice ready for TTS',
    });
  });
});

describe('displayName / hasBookIcon', () => {
  const narratorRow = row({ name: 'Narrator', isNarrator: true });

  it('points the Narrator row at the linked character', () => {
    expect(
      displayName(narratorRow, { characterId: 'watson', displayName: 'Watson', isLinked: true }),
    ).toBe('Narrator → Watson');
    expect(
      displayName(narratorRow, { characterId: 'n', displayName: 'Narrator', isLinked: false }),
    ).toBe('Narrator');
    expect(displayName(row({ name: 'Holmes' }), null)).toBe('Holmes');
  });

  it('shows the book icon on the Narrator and on the narrating character', () => {
    expect(hasBookIcon(narratorRow)).toBe(true);
    expect(hasBookIcon(row({ name: 'Watson', narratesBook: true }))).toBe(true);
    expect(hasBookIcon(row({ name: 'Holmes' }))).toBe(false);
  });
});

describe('filterRows', () => {
  const rows = [
    row({ name: 'Holmes', aliases: [{ id: 'a', name: 'Sherlock' }] }),
    row({ name: 'Watson' }),
  ];

  it('matches name or alias, case-insensitively', () => {
    expect(filterRows(rows, 'sher').map((r) => r.name)).toEqual(['Holmes']);
    expect(filterRows(rows, 'WAT').map((r) => r.name)).toEqual(['Watson']);
    expect(filterRows(rows, '  ').map((r) => r.name)).toEqual(['Holmes', 'Watson']);
  });
});

describe('sortRows', () => {
  const narrator = row({ name: 'Narrator', isNarrator: true, lineCount: 1 });
  const holmes = row({ name: 'Holmes', lineCount: 5, voiceCount: 2, readyVoiceCount: 1 });
  const watson = row({ name: 'Watson', lineCount: 9, voiceCount: 1, readyVoiceCount: 1 });
  const adler = row({ name: 'Adler', lineCount: 5, voiceCount: 0 });
  const rows = [watson, adler, narrator, holmes];

  it('pins the Narrator first and sorts by name', () => {
    expect(sortRows(rows, 'name').map((r) => r.name)).toEqual([
      'Narrator',
      'Adler',
      'Holmes',
      'Watson',
    ]);
  });

  it('sorts by lines descending, ties by name', () => {
    expect(sortRows(rows, 'lines').map((r) => r.name)).toEqual([
      'Narrator',
      'Watson',
      'Adler',
      'Holmes',
    ]);
  });

  it('sorts by readiness descending, no voices last', () => {
    expect(sortRows(rows, 'readiness').map((r) => r.name)).toEqual([
      'Narrator',
      'Watson',
      'Holmes',
      'Adler',
    ]);
  });

  it('does not mutate its input', () => {
    sortRows(rows, 'name');
    expect(rows[0]).toBe(watson);
  });
});
