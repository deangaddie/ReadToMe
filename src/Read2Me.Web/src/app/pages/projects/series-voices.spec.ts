import { describe, expect, it } from 'bun:test';
import type { SeriesCharacterDto, VoiceDto } from '@app/api';
import { deleteSeriesVoiceConfirm } from './permanent-changes';
import {
  defaultCells,
  deleteBlockOf,
  deleteNote,
  renameHint,
  savedInCount,
  savedInLabel,
  seriesCopyBook,
} from './series-voices';

const book = (
  folder: string,
  title: string,
  position: number,
  defaultVoiceId: string | null,
  differs = false,
) => ({
  folder,
  title,
  position,
  characterId: `${folder}-watson`,
  characterName: 'Watson',
  defaultVoiceId,
  differs,
});

const voice = (id: string) => ({ id, name: id }) as VoiceDto;

function detail(overrides: Partial<SeriesCharacterDto> = {}): SeriesCharacterDto {
  return {
    seriesCopyFolder: 'scarlet',
    voices: [voice('v1'), voice('v2')],
    books: [
      book('scarlet', 'A Study in Scarlet', 1, 'v1'),
      book('four', 'The Sign of the Four', 2, 'v2'),
      book('novella', 'A Novella', 2.5, 'v1'),
    ],
    deleteBlocks: {},
    ...overrides,
  };
}

describe('series Voice editor rules', () => {
  it('the series copy is where the writes go', () => {
    expect(seriesCopyBook(detail())?.characterId).toBe('scarlet-watson');
    expect(seriesCopyBook(detail({ seriesCopyFolder: 'gone' }))).toBeNull();
  });

  it('"Saved in N books" counts the linked books that don’t differ', () => {
    expect(savedInCount(detail())).toBe(3);
    expect(savedInLabel(detail())).toBe('Saved in 3 books');
    const differing = detail({
      books: [
        book('scarlet', 'A Study in Scarlet', 1, 'v1'),
        book('four', 'The Sign of the Four', 2, 'v1', true),
      ],
    });
    expect(savedInLabel(differing)).toBe('Saved');
    expect(renameHint(differing)).toBe('Saved in A Study in Scarlet.');
    expect(renameHint(detail())).toBe(
      'Saved in A Study in Scarlet, The Sign of the Four and A Novella.',
    );
  });

  it('the Default in Book N columns mark each book’s own default', () => {
    const cells = defaultCells(detail(), voice('v1'));
    expect(cells.map((c) => [c.heading, c.checked])).toEqual([
      ['Default in Book 1', true],
      ['Default in Book 2', false],
      ['Default in Book 2.5', true],
    ]);
  });

  it('a refused delete reads as a note naming the books', () => {
    expect(deleteNote(null)).toBeNull();
    expect(
      deleteNote({
        reason: 'PositionalRules',
        books: [
          { folder: 'four', title: 'The Sign of the Four', ruleCount: 1 },
          { folder: 'novella', title: 'A Novella', ruleCount: 2 },
        ],
      }),
    ).toBe(
      "Can't delete: Voice Rules in The Sign of the Four and A Novella use it. Remove those rules first.",
    );
    expect(
      deleteNote({
        reason: 'LastVoice',
        books: [{ folder: 'four', title: 'The Sign of the Four', ruleCount: 0 }],
      }),
    ).toBe("Can't delete the last Voice: The Sign of the Four would have no default Voice.");
    const blocked = detail({ deleteBlocks: { v2: { reason: 'LastVoice', books: [] } } });
    expect(deleteBlockOf(blocked, voice('v2'))?.reason).toBe('LastVoice');
    expect(deleteBlockOf(blocked, voice('v1'))).toBeNull();
  });

  it('the delete confirm is red and names every book', () => {
    const confirm = deleteSeriesVoiceConfirm('Army doctor', [
      'A Study in Scarlet',
      'The Sign of the Four',
    ]);
    expect(confirm.title).toBe('Delete "Army doctor" from 2 books?');
    expect(confirm.message).toContain('removed from A Study in Scarlet and The Sign of the Four');
    expect(confirm.destructive).toBe(true);
    expect(confirm.confirmLabel).toBe('Delete Voice');
    expect(deleteSeriesVoiceConfirm('Army doctor', ['A Study in Scarlet']).title).toBe(
      'Delete "Army doctor"?',
    );
  });
});
