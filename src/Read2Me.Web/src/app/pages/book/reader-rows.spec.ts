import { NarratorDto, ParagraphDto, ParagraphItemDto } from '@app/api';
import {
  SpeakerContext,
  buildRows,
  itemSpeaker,
  paragraphSpeaker,
  paragraphText,
  parseMode,
  queueChip,
  reviewChip,
  voiceLine,
} from './reader-rows';

const NARRATOR_ID = '00000000-0000-0000-0000-000000000001';
const UNLINKED: NarratorDto = {
  characterId: NARRATOR_ID,
  displayName: 'Narrator',
  isLinked: false,
};
const LINKED: NarratorDto = { characterId: 'w', displayName: 'Watson', isLinked: true };

let seq = 0;
function item(overrides: Partial<ParagraphItemDto> = {}): ParagraphItemDto {
  seq++;
  return {
    id: `i${seq}`,
    itemType: 'Character',
    text: `text ${seq}`,
    characterId: null,
    audioFileName: null,
    voiceInstructions: null,
    orderKey: `a${seq}`,
    isPause: false,
    ...overrides,
  };
}
const narration = (o: Partial<ParagraphItemDto> = {}) =>
  item({ itemType: 'Narration', characterId: NARRATOR_ID, ...o });
const pause = () => item({ itemType: 'Pause', text: null, isPause: true });
const para = (id: string, items: ParagraphItemDto[], isPauseParagraph = false): ParagraphDto => ({
  id,
  items,
  isPauseParagraph,
});

const ctx = (narrator: NarratorDto = UNLINKED): SpeakerContext => ({
  names: { h: 'Hardin', p: 'Pirenne', w: 'Watson' },
  narrator,
});

describe('parseMode', () => {
  it('accepts the three modes and falls back to read', () => {
    expect(parseMode('speakers')).toBe('speakers');
    expect(parseMode('audio')).toBe('audio');
    expect(parseMode('read')).toBe('read');
    expect(parseMode('combined')).toBe('read');
    expect(parseMode(undefined)).toBe('read');
  });
});

describe('itemSpeaker', () => {
  it.each([
    ['narration, unlinked', narration(), UNLINKED, { state: 'narration', name: 'Narration' }],
    ['narration, linked', narration(), LINKED, { state: 'narration', name: 'Narrator (Watson)' }],
    ['a named character', item({ characterId: 'h' }), UNLINKED, { state: 'named', name: 'Hardin' }],
    ['no speaker', item(), UNLINKED, { state: 'unknown', name: 'Unknown' }],
    [
      'a deleted character',
      item({ characterId: 'gone' }),
      UNLINKED,
      { state: 'unknown', name: 'Unknown' },
    ],
    [
      'the linked narrator character',
      item({ characterId: 'w' }),
      LINKED,
      { state: 'narrator-linked', name: 'Watson' },
    ],
  ])('%s', (_label, it_, narrator, expected) => {
    expect(itemSpeaker(it_, ctx(narrator))).toMatchObject(expected);
  });
});

describe('paragraphSpeaker', () => {
  it.each([
    ['all narration', [narration(), narration()], { state: 'narration', name: 'Narration' }],
    [
      'one speaker with narration',
      [narration(), item({ characterId: 'h' })],
      { state: 'named', name: 'Hardin' },
    ],
    ['all unknown dialog', [item(), narration()], { state: 'unknown', name: 'Unknown' }],
    [
      'one known plus unknown',
      [item({ characterId: 'h' }), item()],
      { state: 'named', name: 'Hardin +?' },
    ],
    [
      'two speakers',
      [item({ characterId: 'h' }), item({ characterId: 'p' })],
      { state: 'mixed', name: 'Mixed' },
    ],
    ['pauses ignored', [pause(), item({ characterId: 'p' })], { state: 'named', name: 'Pirenne' }],
  ])('%s', (_label, items, expected) => {
    expect(paragraphSpeaker(para('p', items), ctx())).toMatchObject(expected);
  });
});

describe('paragraphText', () => {
  it('joins the spoken items, skipping pauses', () => {
    const p = para('p', [item({ text: '"Hi,"' }), pause(), narration({ text: 'he said.' })]);
    expect(paragraphText(p)).toBe('"Hi," he said.');
  });
});

describe('voiceLine', () => {
  it.each([
    [undefined, ''],
    [{ voiceName: null, narratedBy: null }, 'Voice: —'],
    [{ voiceName: 'Deep', narratedBy: null }, 'Voice: Deep'],
    [{ voiceName: 'Deep', narratedBy: 'Watson' }, 'Narrator → Watson · Voice: Deep'],
  ])('%j → %s', (voice, text) => {
    expect(voiceLine(voice)).toBe(text);
  });
});

describe('queueChip', () => {
  it.each([
    [undefined, null],
    [{}, null],
    [{ status: 'Queued' }, { status: 'info', label: 'Queued' }],
    [{ status: 'Processing' }, { status: 'busy', label: 'Processing' }],
    [
      { outcome: { kind: 'Failed', reason: 'boom' } },
      { status: 'error', label: 'Failed', tooltip: 'boom' },
    ],
    [
      { outcome: { kind: 'Unfinished', reason: 'cut off' } },
      { status: 'warn', label: 'Unfinished', tooltip: 'cut off' },
    ],
    [
      { status: 'Queued', outcome: { kind: 'Failed' } },
      { status: 'info', label: 'Queued' },
    ],
  ] as const)('%j', (entry, expected) => {
    const chip = queueChip(entry);
    if (expected === null) expect(chip).toBeNull();
    else expect(chip).toMatchObject(expected);
  });

  it('paragraph attribution calls an unfinished outcome Unknown', () => {
    expect(queueChip({ outcome: { kind: 'Unfinished' } }, 'Unknown')?.label).toBe('Unknown');
  });
});

describe('reviewChip', () => {
  const base = {
    normalizeOk: true,
    normalizeReason: null,
    verifyOk: true,
    wer: null,
    verifyReason: null,
    transcript: null,
    originalTextSnapshot: null,
  };

  it('no review → no chip', () => {
    expect(reviewChip(undefined)).toBeNull();
  });

  it('a normalize failure is an error with its reason', () => {
    expect(
      reviewChip({ ...base, state: 'NeedsReview', normalizeOk: false, normalizeReason: 'clipped' }),
    ).toMatchObject({ status: 'error', label: 'Normalize failed', tooltip: 'clipped' });
  });

  it('a verify failure is a warning with WER and reason', () => {
    expect(
      reviewChip({
        ...base,
        state: 'NeedsReview',
        verifyOk: false,
        wer: 0.42,
        verifyReason: 'too many errors',
      }),
    ).toMatchObject({
      status: 'warn',
      label: 'Verify failed',
      tooltip: 'WER 42% — too many errors',
    });
  });

  it('a dismissed review is neutral', () => {
    expect(reviewChip({ ...base, state: 'Dismissed', verifyOk: false })).toMatchObject({
      status: 'neutral',
      label: 'Review dismissed',
    });
  });
});

describe('buildRows', () => {
  const chapters = [
    {
      id: 'c1',
      title: 'One',
      paragraphs: [para('p1', [narration()]), para('pz', [pause()], true)],
    },
    { id: 'c2', title: 'Two', paragraphs: [para('p2', [item()])] },
  ];

  it('read mode hides pause paragraphs', () => {
    expect(buildRows(chapters, 'read').map((r) => r.key)).toEqual([
      'chapter:c1',
      'paragraph:p1',
      'chapter:c2',
      'paragraph:p2',
    ]);
  });

  it.each(['speakers', 'audio'] as const)(
    '%s mode keeps pause paragraphs as pause rows',
    (mode) => {
      const rows = buildRows(chapters, mode);
      expect(rows.map((r) => r.key)).toEqual([
        'chapter:c1',
        'paragraph:p1',
        'pause:pz',
        'chapter:c2',
        'paragraph:p2',
      ]);
      expect(rows[2]).toMatchObject({ kind: 'pause', chapterId: 'c1', label: 'Pause' });
    },
  );

  it('pause labels name the pause kind; an empty paragraph is a paragraph pause', () => {
    const rows = buildRows(
      [
        {
          id: 'c',
          title: 'C',
          paragraphs: [
            para('a', [item({ itemType: 'ChapterPause', isPause: true, text: null })], true),
            para('b', [], true),
          ],
        },
      ],
      'audio',
    );
    expect(rows.slice(1).map((r) => (r.kind === 'pause' ? r.label : ''))).toEqual([
      'Chapter pause',
      'Paragraph pause',
    ]);
  });
});
