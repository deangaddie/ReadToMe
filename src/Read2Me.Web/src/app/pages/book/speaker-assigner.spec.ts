import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiError, AttributionApi, BookApi } from '@app/api';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { ToastService } from '@app/ui/toast/toast.service';
import { ProjectStore } from '../project/project-store';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';
import { SpeakerAssigner } from './speaker-assigner';

describe('SpeakerAssigner', () => {
  let assigner: SpeakerAssigner;
  let run: ReturnType<typeof vi.fn>;
  let execute: ReturnType<typeof vi.fn>;
  let clearOutcome: ReturnType<typeof vi.fn>;
  let bulkAssignPreview: ReturnType<typeof vi.fn>;
  let characters: ReturnType<typeof vi.fn>;
  let confirm: ReturnType<typeof vi.fn>;
  let toasts: { kind: string; message: unknown }[];
  const paragraphs = signal<Record<string, { outcome?: { kind: 'Failed' } } | null>>({});

  beforeEach(() => {
    run = vi.fn().mockResolvedValue(true);
    execute = vi.fn().mockResolvedValue({ newEntityId: 'c-new' });
    clearOutcome = vi.fn().mockResolvedValue(undefined);
    bulkAssignPreview = vi
      .fn()
      .mockResolvedValue({ paragraphsWithCharacterItems: 2, characterItems: 3 });
    characters = vi.fn().mockResolvedValue([]);
    confirm = vi.fn().mockResolvedValue(true);
    toasts = [];
    paragraphs.set({});
    TestBed.configureTestingModule({
      providers: [
        SpeakerAssigner,
        { provide: BookEditor, useValue: { run, execute } },
        {
          provide: BookStore,
          useValue: {
            folder: signal('dune'),
            speakers: signal({ names: { h: 'Hardin' }, narrator: null }),
          },
        },
        { provide: ProjectStore, useValue: { paragraphs } },
        { provide: BookApi, useValue: { bulkAssignPreview, characters } },
        { provide: AttributionApi, useValue: { clearOutcome } },
        { provide: ConfirmService, useValue: { confirm } },
        {
          provide: ToastService,
          useValue: {
            success: (m: string) => toasts.push({ kind: 'success', message: m }),
            info: (m: string) => toasts.push({ kind: 'info', message: m }),
            problem: (p: unknown) => toasts.push({ kind: 'problem', message: p }),
          },
        },
      ],
    });
    assigner = TestBed.inject(SpeakerAssigner);
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
    expect(run).toHaveBeenCalledWith({ type: 'SetItemCharacter', itemId: 'i1', characterId: 'c-new' });
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
    expect(toasts).toEqual([{ kind: 'success', message: 'Assigned Hardin to 3 lines in 2 paragraphs.' }]);
  });

  it('bulk: a declined confirm writes nothing; no dialog in the selection is an info toast', async () => {
    confirm.mockResolvedValueOnce(false);
    await assigner.assign({ kind: 'selection', paragraphIds: ['p1'] }, null);
    expect(run).not.toHaveBeenCalled();

    bulkAssignPreview.mockResolvedValueOnce({ paragraphsWithCharacterItems: 0, characterItems: 0 });
    await assigner.assign({ kind: 'selection', paragraphIds: ['p1'] }, 'h');
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(toasts).toEqual([{ kind: 'info', message: 'No dialog in the selection — nothing to assign.' }]);
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
