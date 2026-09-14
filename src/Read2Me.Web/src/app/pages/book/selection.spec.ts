import { ParagraphDto, ParagraphItemDto } from '@app/api';
import {
  SelectionMap,
  chapterDialogTotals,
  isDialogParagraph,
  nodeState,
  selectedUnder,
} from './selection';

function item(overrides: Partial<ParagraphItemDto>): ParagraphItemDto {
  return {
    id: 'i',
    itemType: 'Character',
    text: 't',
    characterId: null,
    audioFileName: null,
    voiceInstructions: null,
    orderKey: 'a',
    isPause: false,
    ...overrides,
  };
}

function paragraph(id: string, ...items: ParagraphItemDto[]): ParagraphDto {
  return { id, items, isPauseParagraph: items.every((i) => i.isPause) };
}

const SELECTION: SelectionMap = {
  p1: { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' },
  p2: { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' },
  p3: { chapterId: 'c2', partId: 'pt1', volumeId: 'v1' },
  p9: { chapterId: 'c9', partId: null, volumeId: null },
};

describe('isDialogParagraph', () => {
  it('is true only when a non-pause Character item is present', () => {
    expect(isDialogParagraph(paragraph('p', item({ itemType: 'Character' })))).toBe(true);
    expect(isDialogParagraph(paragraph('p', item({ itemType: 'Character', characterId: 'h' })))).toBe(true);
    expect(isDialogParagraph(paragraph('p', item({ itemType: 'Narration', characterId: 'n' })))).toBe(false);
    expect(isDialogParagraph(paragraph('p', item({ itemType: 'Pause', isPause: true })))).toBe(false);
    expect(isDialogParagraph(paragraph('p'))).toBe(false);
  });
});

describe('selectedUnder', () => {
  it('counts by the level of the node asked about', () => {
    expect(selectedUnder(SELECTION, 'chapter', 'c1')).toBe(2);
    expect(selectedUnder(SELECTION, 'chapter', 'c2')).toBe(1);
    expect(selectedUnder(SELECTION, 'part', 'pt1')).toBe(3);
    expect(selectedUnder(SELECTION, 'volume', 'v1')).toBe(3);
    expect(selectedUnder(SELECTION, 'volume', 'v2')).toBe(0);
  });

  it('a row with unknown part/volume counts under its chapter only', () => {
    expect(selectedUnder(SELECTION, 'chapter', 'c9')).toBe(1);
    expect(selectedUnder({ p9: SELECTION['p9']! }, 'volume', 'v1')).toBe(0);
  });
});

describe('nodeState (tri-state roll-up)', () => {
  it('nothing selected under the node is unchecked, whatever the total', () => {
    expect(nodeState(SELECTION, 'chapter', 'c3', 4)).toBe('unchecked');
    expect(nodeState(SELECTION, 'chapter', 'c3', undefined)).toBe('unchecked');
    expect(nodeState({}, 'volume', 'v1', 0)).toBe('unchecked');
  });

  it('every paragraph the node holds selected is checked', () => {
    expect(nodeState(SELECTION, 'chapter', 'c1', 2)).toBe('checked');
    expect(nodeState(SELECTION, 'part', 'pt1', 3)).toBe('checked');
  });

  it('some of them is indeterminate', () => {
    expect(nodeState(SELECTION, 'chapter', 'c1', 5)).toBe('indeterminate');
    expect(nodeState(SELECTION, 'volume', 'v1', 10)).toBe('indeterminate');
  });

  it('a node whose total is unknown, or zero, cannot read as checked', () => {
    expect(nodeState(SELECTION, 'chapter', 'c1', undefined)).toBe('indeterminate');
    expect(nodeState(SELECTION, 'chapter', 'c1', 0)).toBe('indeterminate');
  });
});

describe('chapterDialogTotals', () => {
  it('counts the Character paragraphs of each loaded chapter', () => {
    const totals = chapterDialogTotals([
      {
        id: 'c1',
        title: 'One',
        paragraphs: [
          paragraph('p1', item({ itemType: 'Narration', characterId: 'n' })),
          paragraph('p2', item({ itemType: 'Character' })),
          paragraph('p3', item({ itemType: 'Pause', isPause: true })),
          paragraph('p4', item({ itemType: 'Narration', characterId: 'n' }), item({ itemType: 'Character', characterId: 'h' })),
        ],
      },
      { id: 'c2', title: 'Two', paragraphs: [] },
    ]);
    expect(totals).toEqual({ c1: 2, c2: 0 });
  });
});
