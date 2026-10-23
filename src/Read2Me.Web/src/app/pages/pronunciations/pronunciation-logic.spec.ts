import { describe, expect, it } from 'bun:test';
import { VIEWING_READ_ONLY } from '@app/api';
import {
  AUDIO_RUNNING,
  countLine,
  deleteBlockedReason,
  filterEntries,
  isDirty,
  saveBlockedReason,
  discardAdvice,
  duplicateWarning,
  overlapNotes,
  savedToast,
} from './pronunciation-logic';

describe('countLine', () => {
  it.each([
    [{ matchCount: 0, itemCount: 0, withAudioCount: 0 }, 'No matches in the book'],
    [{ matchCount: 1, itemCount: 1, withAudioCount: 1 }, '1 match in 1 item · 1 has audio'],
    [{ matchCount: 14, itemCount: 9, withAudioCount: 3 }, '14 matches in 9 items · 3 have audio'],
    [{ matchCount: 2, itemCount: 2, withAudioCount: 0 }, '2 matches in 2 items · 0 have audio'],
  ])('%o reads %p', (reach, text) => {
    expect(countLine(reach)).toBe(text);
  });
});

describe('discardAdvice', () => {
  it.each([
    [0, null],
    [1, "Saving discards the audio of 1 item; it can't be brought back."],
    [12, "Saving discards the audio of 12 items; it can't be brought back."],
  ])('%p items discarded → %p', (count, text) => {
    expect(discardAdvice(count)).toBe(text);
  });
});

describe('overlapNotes', () => {
  const tirith = { id: 't', writtenForm: 'Tirith', spokenForm: 'TEER-ith' };
  const minas = { id: 'm', writtenForm: 'Minas Tirith', spokenForm: 'MEE-nas' };

  it('says the longer entry wins inside it, and the draft wins over a shorter one', () => {
    expect(
      overlapNotes([
        { ...minas, draftWins: false },
        { ...tirith, draftWins: true },
      ]),
    ).toEqual([
      'Inside “Minas Tirith”, that longer entry wins.',
      'Wins over “Tirith” where both match.',
    ]);
  });

  it('is empty without overlaps', () => {
    expect(overlapNotes([])).toEqual([]);
  });
});

describe('duplicateWarning', () => {
  it('names the entry that already has the written form', () => {
    expect(
      duplicateWarning({
        id: 'k',
        writtenForm: 'Kulgan',
        spokenForm: 'KULL-gun',
      }),
    ).toBe('“Kulgan” is already in the list.');
  });

  it('is null without a duplicate', () => {
    expect(duplicateWarning(null)).toBeNull();
  });
});

describe('savedToast', () => {
  it.each([
    [0, 'Saved.'],
    [1, 'Saved. 1 item needs generating.'],
    [12, 'Saved. 12 items need generating.'],
  ])('%p items need audio → %p', (count, text) => {
    expect(savedToast('Saved', count)).toBe(text);
  });

  it('words a delete the same way', () => {
    expect(savedToast('Deleted', 3)).toBe('Deleted. 3 items need generating.');
  });
});

describe('isDirty', () => {
  const saved = { writtenForm: 'Kulgan', spokenForm: 'KULL-gun' };

  it.each([
    [null, { writtenForm: '', spokenForm: '' }, false],
    [null, { writtenForm: 'Pug', spokenForm: '' }, true],
    [saved, { writtenForm: 'Kulgan', spokenForm: 'KULL-gun' }, false],
    [saved, { writtenForm: ' Kulgan ', spokenForm: 'KULL-gun' }, false],
    [saved, { writtenForm: 'Kulgan', spokenForm: 'KULL-gn' }, true],
    [saved, { writtenForm: 'kulgan', spokenForm: 'KULL-gun' }, true],
  ])('saved %o, draft %o → %p', (original, draft, dirty) => {
    expect(isDirty(original, draft)).toBe(dirty);
  });
});

describe('saveBlockedReason', () => {
  const free = {
    readOnly: null,
    audioInFlight: 0,
    duplicateOf: null,
    invalid: null,
  };

  it('is null when nothing stops a save', () => {
    expect(saveBlockedReason(free)).toBeNull();
  });

  it.each([
    [{ ...free, readOnly: VIEWING_READ_ONLY, audioInFlight: 3 }, VIEWING_READ_ONLY],
    [{ ...free, audioInFlight: 3 }, AUDIO_RUNNING],
    [
      {
        ...free,
        duplicateOf: { id: 'k', writtenForm: 'Kulgan', spokenForm: 'x' },
      },
      '“Kulgan” is already in the list.',
    ],
    [
      { ...free, invalid: 'Written form must start with a letter.' },
      'Written form must start with a letter.',
    ],
  ])('%o → %p', (gate, reason) => {
    expect(saveBlockedReason(gate)).toBe(reason);
  });
});

describe('deleteBlockedReason', () => {
  it.each([
    [{ readOnly: null, audioInFlight: 0 }, null],
    [{ readOnly: null, audioInFlight: 1 }, AUDIO_RUNNING],
    [{ readOnly: VIEWING_READ_ONLY, audioInFlight: 1 }, VIEWING_READ_ONLY],
  ])('%o → %p', (gate, reason) => {
    expect(deleteBlockedReason(gate)).toBe(reason);
  });

  it('names audio generation as the host does', () => {
    expect(AUDIO_RUNNING).toBe('Audio generation is running: wait for it or cancel it');
  });
});

describe('filterEntries', () => {
  const entries = [
    { writtenForm: 'Kulgan', spokenForm: 'KULL-gun' },
    { writtenForm: 'Pug', spokenForm: 'pug' },
    { writtenForm: 'Crydee', spokenForm: 'CRY-dee' },
  ];

  it.each([
    ['', ['Kulgan', 'Pug', 'Crydee']],
    ['kul', ['Kulgan']],
    ['  CRY ', ['Crydee']],
    ['pug', ['Pug']],
    ['-gun', ['Kulgan']],
    ['zz', []],
  ])('%p finds %p, by written or spoken form', (query, found) => {
    expect(filterEntries(entries, query).map((e) => e.writtenForm)).toEqual(found);
  });
});
