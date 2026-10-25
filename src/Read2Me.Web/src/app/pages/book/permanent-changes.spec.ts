import { describe, expect, it } from 'bun:test';
import type { ParagraphDto, ParagraphItemDto } from '@app/api';
import type { NodeMenuTarget } from './node-menu-entries';
import {
  addPausesChange,
  attributeSelectionChange,
  generateAudioChange,
  mergeChange,
  paragraphSweep,
  speakerChange,
} from './permanent-changes';

function item(overrides: Partial<ParagraphItemDto> = {}): ParagraphItemDto {
  return {
    id: 'i',
    itemType: 'Character',
    text: 'Hello.',
    characterId: 'h',
    audioFileName: null,
    voiceInstructions: null,
    orderKey: 'a',
    isPause: false,
    ...overrides,
  };
}

const narration = (overrides: Partial<ParagraphItemDto> = {}) =>
  item({ itemType: 'Narration', characterId: 'n', ...overrides });
const unassigned = (overrides: Partial<ParagraphItemDto> = {}) =>
  item({ characterId: null, ...overrides });
const pause = () => item({ itemType: 'Pause', isPause: true, text: null, characterId: null });

function paragraph(items: ParagraphItemDto[], id = 'p'): ParagraphDto {
  return { id, items, isPauseParagraph: false };
}

function target(overrides: Partial<NodeMenuTarget> = {}): NodeMenuTarget {
  return {
    kind: 'chapter',
    id: 'c1',
    text: 'Jonathan Harker’s Journal',
    isFirst: false,
    isLast: false,
    ...overrides,
  };
}

describe('addPausesChange', () => {
  it.each([
    [3, 'Inserts a pause between paragraphs and at the end of every chapter (3 chapters).'],
    [1, 'Inserts a pause between paragraphs and at the end of every chapter (1 chapter).'],
    [0, 'Inserts a pause between paragraphs and at the end of every chapter (0 chapters).'],
  ])('always asks, neutral, counting %i chapters', (chapters, scope) => {
    expect(addPausesChange(chapters)).toEqual({
      action: 'Add pauses',
      scope,
      cost: 'Removing them later means deleting each one.',
      destructive: false,
    });
  });
});

describe('mergeChange', () => {
  it('Previous folds this node into the one before it; neutral', () => {
    expect(mergeChange(target(), 'Previous')).toEqual({
      action: 'Merge with previous',
      scope: 'Folds “Jonathan Harker’s Journal” into the previous chapter.',
      cost: 'Splitting it back needs the exact point.',
      destructive: false,
    });
  });

  it('Next folds the node after it into this one', () => {
    expect(mergeChange(target({ kind: 'part', text: 'Part One' }), 'Next').scope).toBe(
      'Folds the next part into “Part One”.',
    );
  });

  it.each([
    ['volume', 'this volume'],
    ['paragraph', 'this paragraph'],
  ] as const)('an untitled %s is named by its kind', (kind, name) => {
    expect(mergeChange(target({ kind, text: null }), 'Previous').scope).toBe(
      `Folds ${name} into the previous ${kind}.`,
    );
  });

  it('shortens a long paragraph text the way the delete confirm does', () => {
    const text = 'x'.repeat(80);
    expect(mergeChange(target({ kind: 'paragraph', text }), 'Next').scope).toBe(
      `Folds the next paragraph into “${'x'.repeat(60)}…”.`,
    );
  });

  it('an item merge is red and says whose speaker and audio are lost', () => {
    const previous = mergeChange(target({ kind: 'item', text: 'Hello.' }), 'Previous');
    expect(previous).toEqual({
      action: 'Merge with previous',
      scope: 'Folds “Hello.” into the previous item. This item’s speaker and audio are discarded.',
      cost: 'Splitting it back needs the exact point.',
      destructive: true,
    });
    expect(mergeChange(target({ kind: 'item', text: 'Hello.' }), 'Next').scope).toBe(
      'Folds the next item into “Hello.”. The next item’s speaker and audio are discarded.',
    );
  });
});

describe('attributeSelectionChange', () => {
  it.each([
    ['nothing selected', []],
    [
      'no selected paragraph has an assigned dialog speaker',
      [paragraph([narration(), unassigned()])],
    ],
    // The queue's attribution leaves audio alone and never touches narration (ADR-0006).
    [
      'only audio, on narration or an unassigned line',
      [paragraph([narration({ audioFileName: 'n.wav' }), unassigned({ audioFileName: 'a.wav' })])],
    ],
  ])('is null when %s', (_case, paragraphs) => {
    expect(attributeSelectionChange(paragraphs)).toBeNull();
  });

  it.each([
    [
      '3 paragraphs, 2 speakers',
      [
        paragraph([narration(), item({ id: 'a' }), item({ id: 'b', audioFileName: 'b.wav' })]),
        paragraph([unassigned({ id: 'c' })], 'q'),
        paragraph([narration({ audioFileName: 'n.wav' })], 'r'),
      ],
      'Re-attributes 3 paragraphs: 2 assigned speakers are replaced.',
    ],
    [
      '1 paragraph, 1 speaker',
      [paragraph([item()])],
      'Re-attributes 1 paragraph: 1 assigned speaker is replaced.',
    ],
  ])(
    'counts the paragraphs and the assigned dialog speakers (%s); red',
    (_case, paragraphs, scope) => {
      expect(attributeSelectionChange(paragraphs)).toEqual({
        action: 'Attribute',
        scope,
        cost: 'Getting a replaced speaker back means assigning it again by hand.',
        destructive: true,
      });
    },
  );
});

describe('generateAudioChange', () => {
  it.each([
    ['nothing selected', []],
    ['no selected item has a take', [item(), narration()]],
  ])('is null when %s', (_case, items) => {
    expect(generateAudioChange(items)).toBeNull();
  });

  it.each([
    [
      '2 of 3',
      [
        item({ audioFileName: 'a.wav' }),
        item({ id: 'b' }),
        narration({ id: 'c', audioFileName: 'c.wav' }),
      ],
      'Replaces existing audio on 2 items.',
    ],
    ['1 of 1', [item({ audioFileName: 'a.wav' })], 'Replaces existing audio on 1 item.'],
  ])('counts the items with a take (%s); red', (_case, items, scope) => {
    expect(generateAudioChange(items)).toEqual({
      action: 'Generate audio',
      scope,
      cost: 'The current takes cannot be brought back.',
      destructive: true,
    });
  });
});

describe('speakerChange', () => {
  it.each([
    ['no item has audio', [item(), item({ id: 'b' })], 'g'],
    // An item already on the target speaker keeps its audio (the host's idempotent stamp).
    ['the item is already on the speaker', [item({ audioFileName: 'a.wav' })], 'h'],
    ['a clear meets no speaker', [unassigned({ audioFileName: 'a.wav' })], null],
    ['only a pause', [pause()], 'g'],
  ])('is null when %s', (_case, items, characterId) => {
    expect(speakerChange(items, characterId)).toBeNull();
  });

  it.each([
    [
      'assign, 1 of 3 moves',
      [
        item({ audioFileName: 'a.wav' }),
        item({ id: 'b', audioFileName: 'b.wav', characterId: 'g' }),
        item({ id: 'c' }),
      ],
      'g',
      {
        action: 'Assign',
        scope: 'Discards audio on 1 item.',
        cost: 'It must be generated again for the new speaker.',
      },
    ],
    [
      'clear, both lose',
      [item({ audioFileName: 'a.wav' }), item({ id: 'b', audioFileName: 'b.wav' })],
      null,
      {
        action: 'Clear',
        scope: 'Discards audio on 2 items.',
        cost: 'It must be generated again once the line has a speaker.',
      },
    ],
  ])(
    'counts the items whose speaker moves and whose audio goes (%s); red',
    (_case, items, characterId, expected) => {
      expect(speakerChange(items, characterId)).toEqual({ ...expected, destructive: true });
    },
  );
});

describe('paragraphSweep', () => {
  it('stamps the dialog items, or every speech item when the paragraph holds no dialog', () => {
    const n = narration();
    const a = item({ id: 'a' });
    expect(paragraphSweep(paragraph([n, a, pause()]))).toEqual([a]);
    expect(paragraphSweep(paragraph([n, pause()]))).toEqual([n]);
  });
});
