import { beforeEach, describe, expect, it, mock } from 'bun:test';
import type { ParagraphDto, ParagraphItemDto } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { Preflight } from '@app/shared/preflight';
import { type ConfirmOptions, ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
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
const BASE = '/api/projects/dracula';
const REF = (id: string, chapterId = 'c1') => ({ id, chapterId, partId: 'pt1', volumeId: 'v1' });
const ITEM_REF = (id: string, paragraphId: string) => ({ ...REF(id), paragraphId });

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
  let api: FakeApi;
  let toasts: string[];
  let ensureReady: ReturnType<typeof mock>;
  /** Every confirm the workflow asked for, answered with {@link confirmAnswer}. */
  let confirms: ConfirmOptions[];
  let confirmAnswer: boolean;
  let releaseNarration: ReturnType<typeof mock>;
  let enqueueItems: ReturnType<typeof mock>;
  let enqueueNode: ReturnType<typeof mock>;

  beforeEach(() => {
    resetServices();
    api = new FakeApi();
    api.install();
    toasts = [];
    ensureReady = mock(async () => true);
    override(Preflight, { ensureReady } as unknown as Preflight);
    confirms = [];
    confirmAnswer = true;
    override(ConfirmService, {
      confirm: async (o: ConfirmOptions) => (confirms.push(o), confirmAnswer),
    } as unknown as ConfirmService);
    override(ToastService, {
      success: (m: string) => toasts.push(m),
      info: (m: string) => toasts.push(m),
      problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
    } as unknown as ToastService);
    releaseNarration = mock(async () => true);
    enqueueItems = mock(async () => true);
    enqueueNode = mock(async () => true);

    mode = signal('read');
    chapters = signal<LoadedChapterView[]>([]);
    detail = signal({ narratorOnlyMode: false });
    const folder = signal('dracula');
    const tree = signal([node('v1', 'volume', [node('pt1', 'part', [node('c1', 'chapter')])])]);
    selection = new SelectionStore();
    audioSelection = new AudioSelectionStore();
    workflow = new SelectionWorkflow(
      { mode, chapters, tree, folder } as unknown as BookStore,
      { detail } as unknown as ProjectStore,
      selection,
      audioSelection,
      { releaseNarration } as unknown as SpeakerAssigner,
      { enqueueItems, enqueueNode } as unknown as AudioGenerator,
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

  describe('toggleNode', () => {
    it('reads the node paragraph ids and selects them; unticking removes exactly what it reads', async () => {
      api.on('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`, [REF('p1'), REF('p2')]);
      await workflow.toggleNode(node('c1', 'chapter'), true);
      expect(selection.ids()).toEqual(['p1', 'p2']);
      expect(selection.nodeState('chapter', 'c1')).toBe('checked');
      expect(workflow.working()).toBe(false);

      api.on('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`, [REF('p1')]);
      await workflow.toggleNode(node('c1', 'chapter'), false);
      expect(selection.ids()).toEqual(['p2']);
      expect(api.calls('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`)).toHaveLength(2);
    });

    it('in Audio mode reads the node item ids into the item selection', async () => {
      mode.set('audio');
      api.on('GET', `${BASE}/nodes/chapter/c1/item-ids`, [ITEM_REF('i1', 'p1')]);
      await workflow.toggleNode(node('c1', 'chapter'), true);
      expect(audioSelection.ids()).toEqual(['i1']);
      expect(audioSelection.nodeState('chapter', 'c1')).toBe('checked');
      expect(selection.count()).toBe(0);
    });
  });

  describe('attribute', () => {
    it('gates on preflight, posts the selected ids, reports the count and clears the selection', async () => {
      api.on('POST', `${BASE}/attribution/enqueue-paragraphs`, { enqueued: 1 });
      chapters.set([C1]);
      selection.toggle('p2', ANCESTRY, true);

      await workflow.attribute();

      expect(ensureReady).toHaveBeenCalledWith('attribution');
      expect(
        api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`).map((r) => r.body),
      ).toEqual([{ paragraphIds: ['p2'] }]);
      expect(toasts).toEqual(['Queued 1 paragraph']);
      expect(selection.count()).toBe(0);
      // Nothing selected had an assigned speaker: no permanent-change confirm.
      expect(confirms).toEqual([]);
      expect(workflow.working()).toBe(false);
    });

    it('confirms when a selected paragraph has an assigned speaker, reading chapters not in the window', async () => {
      api.on('POST', `${BASE}/attribution/enqueue-paragraphs`, { enqueued: 2 });
      api.on('GET', `${BASE}/nodes/chapter/c9/children`, {
        paragraphs: [paragraph('p9', item('i9', { characterId: 'harker' }))],
      });
      chapters.set([C1]);
      selection.toggle('p2', ANCESTRY, true);
      selection.toggle('p9', { ...ANCESTRY, chapterId: 'c9' }, true);

      confirmAnswer = false;
      await workflow.attribute();
      expect(confirms).toEqual([
        {
          title: 'Attribute?',
          message:
            'Re-attributes 2 paragraphs: 1 assigned speaker is replaced. ' +
            'Getting a replaced speaker back means assigning it again by hand.',
          confirmLabel: 'Attribute',
          destructive: true,
        },
      ]);
      expect(api.calls('GET', `${BASE}/nodes/chapter/c9/children`)).toHaveLength(1);
      expect(api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`)).toHaveLength(0);
      expect(selection.count()).toBe(2);

      confirmAnswer = true;
      await workflow.attribute();
      expect(
        api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`).map((r) => r.body),
      ).toEqual([{ paragraphIds: ['p2', 'p9'] }]);
      expect(selection.count()).toBe(0);
    });

    it('a cancelled preflight sends nothing and keeps the selection', async () => {
      ensureReady.mockResolvedValueOnce(false);
      chapters.set([C1]);
      selection.toggle('p2', ANCESTRY, true);
      await workflow.attribute();
      expect(api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`)).toHaveLength(0);
      expect(selection.count()).toBe(1);
      expect(toasts).toEqual([]);
    });
  });

  describe('nodeAction', () => {
    const C1_NODE = { level: 'chapter', id: 'c1', title: 'Jonathan Harker’s Journal' } as const;

    it('Select unprocessed and Attribute unprocessed act on the node', async () => {
      api
        .on('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`, (_body, url) =>
          url.searchParams.get('unprocessedOnly') === 'true' ? [REF('p2')] : [],
        )
        .on('POST', `${BASE}/attribution/enqueue`, { enqueued: 2 });

      await workflow.nodeAction(C1_NODE, 'select-unprocessed');
      expect(selection.ids()).toEqual(['p2']);

      await workflow.nodeAction(C1_NODE, 'attribute-node');
      expect(ensureReady).toHaveBeenCalledTimes(1);
      expect(api.calls('POST', `${BASE}/attribution/enqueue`).map((r) => r.body)).toEqual([
        { level: 'chapter', nodeId: 'c1', unprocessedOnly: true },
      ]);
      expect(toasts).toEqual(['Queued 2 paragraphs']);
    });

    it('Release narration releases the node, then clears both selections and their stale totals', async () => {
      chapters.set([C1]);
      selection.toggle('p1', ANCESTRY, true);
      selection.learnTotals({ 'part-x': 3 });
      audioSelection.toggle('i1', ANCESTRY, true);

      await workflow.nodeAction(C1_NODE, 'release-narration');

      expect(releaseNarration).toHaveBeenCalledWith('Chapter', 'c1', 'Jonathan Harker’s Journal');
      expect(selection.count()).toBe(0);
      expect(audioSelection.count()).toBe(0);
      expect(selection.totals()['part-x']).toBeUndefined();
      // The loaded chapter's totals are relearnt straight away.
      expect(selection.totals()).toEqual({ c1: 2 });
      expect(audioSelection.totals()).toEqual({ c1: 1 });
      // The release itself never queues attribution.
      expect(api.calls('POST', `${BASE}/attribution/enqueue`)).toHaveLength(0);
    });

    it('a release that writes nothing keeps the selection', async () => {
      releaseNarration.mockResolvedValueOnce(false);
      selection.toggle('p1', ANCESTRY, true);
      await workflow.nodeAction(C1_NODE, 'release-narration');
      expect(selection.count()).toBe(1);
    });

    it('Select needs audio reads with the filter and narrator-only flag', async () => {
      detail.set({ narratorOnlyMode: true });
      api.on('GET', `${BASE}/nodes/chapter/c1/item-ids`, (_body, url) =>
        url.searchParams.get('needsAudioOnly') === 'true' &&
        url.searchParams.get('narratorOnlyMode') === 'true'
          ? [ITEM_REF('i3', 'p3')]
          : [],
      );
      await workflow.nodeAction(C1_NODE, 'select-needs-audio');
      expect(audioSelection.ids()).toEqual(['i3']);
    });

    it('Generate audio for this node hands the node to the generator', async () => {
      await workflow.nodeAction(C1_NODE, 'generate-audio-node');
      expect(enqueueNode).toHaveBeenCalledWith('chapter', 'c1', false);
    });
  });

  it('releaseParagraph releases that paragraph by its text and clears the selection', async () => {
    selection.toggle('p1', ANCESTRY, true);
    await workflow.releaseParagraph('q1', 'Q: Your name?');
    expect(releaseNarration).toHaveBeenCalledWith('Paragraph', 'q1', 'Q: Your name?');
    expect(selection.count()).toBe(0);
  });

  describe('generate', () => {
    it('queues the selected items through the generator and clears them', async () => {
      chapters.set([C1]);
      audioSelection.toggle('i1', ANCESTRY, true);
      await workflow.generate();
      expect(enqueueItems).toHaveBeenCalledWith(['i1']);
      expect(audioSelection.count()).toBe(0);
      expect(confirms).toEqual([]);
    });

    it('confirms when a selected item already has a take', async () => {
      chapters.set([
        { id: 'c1', paragraphs: [paragraph('p1', item('i1', { audioFileName: 'i1.wav' }))] },
      ] as unknown as LoadedChapterView[]);
      audioSelection.toggle('i1', ANCESTRY, true);

      confirmAnswer = false;
      await workflow.generate();
      expect(confirms).toEqual([
        {
          title: 'Generate audio?',
          message: 'Replaces existing audio on 1 item. The current takes cannot be brought back.',
          confirmLabel: 'Generate audio',
          destructive: true,
        },
      ]);
      expect(enqueueItems).not.toHaveBeenCalled();
      expect(audioSelection.count()).toBe(1);

      confirmAnswer = true;
      await workflow.generate();
      expect(enqueueItems).toHaveBeenCalledWith(['i1']);
      expect(audioSelection.count()).toBe(0);
    });
  });
});
