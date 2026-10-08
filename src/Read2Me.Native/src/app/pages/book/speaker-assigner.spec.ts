import { beforeEach, describe, expect, it, mock } from 'bun:test';
import { ApiError, AttributionApi, BookApi } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import type { ParagraphStatusEntry } from '@app/live/live-messages';
import type { ProjectStore } from '../project/project-store';
import type { BookEditor } from './book-editor';
import type { BookStore } from './book-store';
import { SpeakerAssigner } from './speaker-assigner';

/**
 * Ported from the Angular TestBed spec, case for case: the app-wide collaborators go in through
 * `override()`, the page-scoped ones (editor, stores) through the constructor.
 */
describe('SpeakerAssigner', () => {
  let assigner: SpeakerAssigner;
  let run: ReturnType<typeof mock>;
  let execute: ReturnType<typeof mock>;
  let clearOutcome: ReturnType<typeof mock>;
  let bulkAssignPreview: ReturnType<typeof mock>;
  let characters: ReturnType<typeof mock>;
  let confirm: ReturnType<typeof mock>;
  let promptText: ReturnType<typeof mock>;
  let toasts: { kind: string; message: unknown }[];
  const paragraphs = signal<Record<string, ParagraphStatusEntry | null>>({});

  beforeEach(() => {
    resetServices();
    run = mock(async () => true);
    execute = mock(async () => ({ newEntityId: 'c-new' }));
    clearOutcome = mock(async () => undefined);
    bulkAssignPreview = mock(async () => ({ paragraphsWithCharacterItems: 2, characterItems: 3 }));
    characters = mock(async () => []);
    confirm = mock(async () => true);
    promptText = mock(async () => null);
    toasts = [];
    paragraphs.set({});
    override(BookApi, { bulkAssignPreview, characters } as unknown as BookApi);
    override(AttributionApi, { clearOutcome } as unknown as AttributionApi);
    override(ConfirmService, { confirm } as unknown as ConfirmService);
    override(PromptService, { text: promptText } as unknown as PromptService);
    override(ToastService, {
      success: (m: string) => toasts.push({ kind: 'success', message: m }),
      info: (m: string) => toasts.push({ kind: 'info', message: m }),
      problem: (p: unknown) => toasts.push({ kind: 'problem', message: p }),
    } as unknown as ToastService);
    assigner = new SpeakerAssigner(
      { run, execute } as unknown as BookEditor,
      {
        folder: signal('dune'),
        speakers: signal({ names: { h: 'Hardin' }, narrator: null }),
      } as unknown as BookStore,
      { paragraphs } as unknown as ProjectStore,
    );
  });

  it('an item pick posts SetItemCharacter, then forgets the paragraph outcome', async () => {
    await assigner.assign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, 'h');
    expect(run).toHaveBeenCalledWith({ type: 'SetItemCharacter', itemId: 'i1', characterId: 'h' });
    expect(clearOutcome).toHaveBeenCalledWith('dune', 'p1');
  });

  it('a paragraph clear posts a null speaker; a refused write clears no outcome', async () => {
    await assigner.assign({ kind: 'paragraph', paragraphId: 'p1' }, null);
    expect(run).toHaveBeenCalledWith({
      type: 'SetParagraphCharacter',
      paragraphId: 'p1',
      characterId: null,
    });
    expect(clearOutcome).toHaveBeenCalledTimes(1);

    run.mockResolvedValueOnce(false);
    await assigner.assign({ kind: 'paragraph', paragraphId: 'p2' }, 'h');
    expect(clearOutcome).toHaveBeenCalledTimes(1);
  });

  it('new character: creates by name and assigns the id the host resolved', async () => {
    await assigner.createAndAssign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, '  Gaal ');
    expect(execute).toHaveBeenCalledWith({ type: 'CreateCharacter', name: 'Gaal' });
    expect(run).toHaveBeenCalledWith({
      type: 'SetItemCharacter',
      itemId: 'i1',
      characterId: 'c-new',
    });
  });

  it('new character with a blank search asks for the name, then creates and assigns', async () => {
    promptText.mockResolvedValueOnce('Gaal');
    await assigner.createAndAssign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, '  ');
    expect(promptText).toHaveBeenCalledTimes(1);
    expect(execute).toHaveBeenCalledWith({ type: 'CreateCharacter', name: 'Gaal' });
    expect(run).toHaveBeenCalledWith({
      type: 'SetItemCharacter',
      itemId: 'i1',
      characterId: 'c-new',
    });
  });

  it('new character: a cancelled name prompt writes nothing', async () => {
    await assigner.createAndAssign({ kind: 'paragraph', paragraphId: 'p1' }, '');
    expect(promptText).toHaveBeenCalledTimes(1);
    expect(execute).not.toHaveBeenCalled();
    expect(run).not.toHaveBeenCalled();
  });

  it('new character with no id answered resolves it from the roster by name or alias', async () => {
    execute.mockResolvedValueOnce({ newEntityId: null });
    characters.mockResolvedValueOnce([
      { id: 'h', name: 'Hardin', aliases: [{ id: 'a', name: 'Salvor' }] },
    ]);
    await assigner.createAndAssign({ kind: 'paragraph', paragraphId: 'p1' }, 'salvor');
    expect(run).toHaveBeenCalledWith({
      type: 'SetParagraphCharacter',
      paragraphId: 'p1',
      characterId: 'h',
    });

    execute.mockResolvedValueOnce(undefined);
    await assigner.createAndAssign({ kind: 'paragraph', paragraphId: 'p1' }, '');
    await assigner.createAndAssign({ kind: 'paragraph', paragraphId: 'p1' }, 'Refused');
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('bulk: previews, confirms with the wording, writes once and clears only remembered outcomes', async () => {
    paragraphs.set({ p1: { outcome: { kind: 'Failed' } }, p2: null });
    await assigner.assign({ kind: 'selection', paragraphIds: ['p1', 'p2', 'p3'] }, 'h');

    expect(bulkAssignPreview).toHaveBeenCalledWith('dune', ['p1', 'p2', 'p3']);
    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Assign Hardin to selection',
        message:
          'Hardin becomes the speaker for 3 dialog lines in 2 paragraphs. Existing speakers are ' +
          'replaced. 1 selected paragraph have no dialog and stay unchanged.',
      }),
    );
    expect(run).toHaveBeenCalledWith({
      type: 'SetParagraphsCharacter',
      paragraphIds: ['p1', 'p2', 'p3'],
      characterId: 'h',
    });
    expect(clearOutcome.mock.calls).toEqual([['dune', 'p1']]);
    expect(toasts).toEqual([
      { kind: 'success', message: 'Assigned Hardin to 3 lines in 2 paragraphs.' },
    ]);
  });

  it('bulk: a declined confirm writes nothing; no dialog in the selection is an info toast', async () => {
    confirm.mockResolvedValueOnce(false);
    await assigner.assign({ kind: 'selection', paragraphIds: ['p1'] }, null);
    expect(run).not.toHaveBeenCalled();

    bulkAssignPreview.mockResolvedValueOnce({ paragraphsWithCharacterItems: 0, characterItems: 0 });
    await assigner.assign({ kind: 'selection', paragraphIds: ['p1'] }, 'h');
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(toasts).toEqual([
      { kind: 'info', message: 'No dialog in the selection — nothing to assign.' },
    ]);
  });

  it('a failed preview or outcome clear becomes a problem toast', async () => {
    bulkAssignPreview.mockRejectedValueOnce(new ApiError(500, 'Boom', 'preview broke'));
    await assigner.assign({ kind: 'selection', paragraphIds: ['p1'] }, 'h');
    clearOutcome.mockRejectedValueOnce(new ApiError(500, 'Boom', 'clear broke'));
    await assigner.clearOutcome('p9');
    expect(toasts.map((t) => (t.message as { detail: string }).detail)).toEqual([
      'preview broke',
      'clear broke',
    ]);
  });
});
