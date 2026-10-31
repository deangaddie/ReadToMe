import { beforeEach, describe, expect, it } from 'bun:test';
import type { ParagraphDto, ParagraphItemDto } from '@app/api';
import { resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { ProjectStore } from '../project/project-store';
import type { AudioGenerator } from './audio-generator';
import type { BookStore } from './book-store';
import type { TreeNode } from './book-tree';
import type { LoadedChapterView } from './reader-rows';
import { AudioSelectionStore, SelectionStore } from './selection-store';
import { SelectionWorkflow } from './selection-workflow';
import type { SpeakerAssigner } from './speaker-assigner';

function item(id: string, overrides: Partial<ParagraphItemDto> = {}): ParagraphItemDto {
  return {
    id,
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
  return { id, items, isPauseParagraph: false };
}

function node(id: string, level: TreeNode['level'], children: TreeNode[] = []): TreeNode {
  return {
    id,
    level,
    title: id,
    rawTitle: id,
    untitled: false,
    isFirst: true,
    isLast: true,
    expandable: level !== 'chapter',
    loaded: true,
    loadTarget: { level, id },
    children,
  };
}

const ANCESTRY = { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' } as const;

/** c1: a Character paragraph with a speaker, a Character paragraph without, and narration. */
const C1 = {
  id: 'c1',
  paragraphs: [
    paragraph('p1', item('i1', { characterId: 'harker' })),
    paragraph('p2', item('i2')),
    paragraph('p3', item('i3', { itemType: 'Narration' })),
  ],
} as unknown as LoadedChapterView;

describe('SelectionWorkflow', () => {
  let mode: ReturnType<typeof signal<string>>;
  let chapters: ReturnType<typeof signal<LoadedChapterView[]>>;
  let detail: ReturnType<typeof signal<{ narratorOnlyMode: boolean }>>;
  let selection: SelectionStore;
  let audioSelection: AudioSelectionStore;
  let workflow: SelectionWorkflow;

  beforeEach(() => {
    resetServices();
    mode = signal('read');
    chapters = signal<LoadedChapterView[]>([]);
    detail = signal({ narratorOnlyMode: false });
    const tree = signal([node('v1', 'volume', [node('pt1', 'part', [node('c1', 'chapter')])])]);
    selection = new SelectionStore();
    audioSelection = new AudioSelectionStore();
    workflow = new SelectionWorkflow(
      { mode, chapters, tree } as unknown as BookStore,
      { detail } as unknown as ProjectStore,
      selection,
      audioSelection,
      {} as SpeakerAssigner,
      {} as AudioGenerator,
    );
  });

  it('the kind follows the mode: items in Audio, paragraphs otherwise', () => {
    expect(workflow.kind()).toBe('paragraphs');
    mode.set('audio');
    expect(workflow.kind()).toBe('items');
    mode.set('speakers');
    expect(workflow.kind()).toBe('paragraphs');
  });

  it('nodeStates rolls up the active selection over every tree node', () => {
    selection.learnTotals({ c1: 2 });
    selection.toggle('p1', ANCESTRY, true);
    expect(workflow.nodeStates()).toEqual({
      v1: 'indeterminate',
      pt1: 'indeterminate',
      c1: 'indeterminate',
    });
    selection.toggle('p2', ANCESTRY, true);
    expect(workflow.nodeStates()['c1']).toBe('checked');

    mode.set('audio');
    expect(workflow.nodeStates()).toEqual({ v1: 'unchecked', pt1: 'unchecked', c1: 'unchecked' });
    audioSelection.toggle('i1', ANCESTRY, true);
    expect(workflow.nodeStates()['c1']).toBe('indeterminate');
  });

  it('a loaded chapter teaches both selections their totals', () => {
    chapters.set([C1]);
    expect(selection.totals()).toEqual({ c1: 2 });
    expect(audioSelection.totals()).toEqual({ c1: 1 });
  });

  it('narrator-only mode counts every spoken item as voiced', () => {
    detail.set({ narratorOnlyMode: true });
    chapters.set([C1]);
    expect(audioSelection.totals()).toEqual({ c1: 3 });
    detail.set({ narratorOnlyMode: false });
    expect(audioSelection.totals()).toEqual({ c1: 1 });
  });

  it('dispose stops the learning', () => {
    workflow.dispose();
    chapters.set([C1]);
    expect(selection.totals()).toEqual({});
    expect(audioSelection.totals()).toEqual({});
  });
});
