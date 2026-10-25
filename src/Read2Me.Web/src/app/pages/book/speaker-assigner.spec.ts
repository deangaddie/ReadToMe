import { beforeEach, describe, expect, it, mock } from 'bun:test';
import { ApiError, AttributionApi, BookApi, type ParagraphItemDto } from '@app/api';
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
  let releaseNarrationPreview: ReturnType<typeof mock>;
  let confirm: ReturnType<typeof mock>;
  let promptText: ReturnType<typeof mock>;
  let toasts: { kind: string; message: unknown }[];
  const paragraphs = signal<Record<string, ParagraphStatusEntry | null>>({});
  /** The loaded window: p1 holds narration with audio, a voiced line with audio and a bare line. */
  const speech = (
    id: string,
    itemType: 'Narration' | 'Character',
    characterId: string | null,
    audioFileName: string | null,
  ): ParagraphItemDto => ({
    id,
    itemType,
    text: id,
    characterId,
    audioFileName,
    voiceInstructions: null,
    orderKey: id,
    isPause: false,
  });
  const chapters = signal([
    {
      id: 'c1',
      paragraphs: [
        {
          id: 'p1',
          isPauseParagraph: false,
          items: [
            speech('n1', 'Narration', 'n', 'n1.wav'),
            speech('i1', 'Character', 'h', 'i1.wav'),
            speech('i2', 'Character', null, null),
          ],
        },
      ],
    },
  ]);

  beforeEach(() => {
    resetServices();
    run = mock(async () => true);
    execute = mock(async () => ({ newEntityId: 'c-new' }));
    clearOutcome = mock(async () => undefined);
    bulkAssignPreview = mock(async () => ({ paragraphsWithCharacterItems: 2, characterItems: 3 }));
    releaseNarrationPreview = mock(async () => ({
      paragraphs: 41,
      itemsWithAudio: 12,
      speechMarkers: [
        { marker: 'Q.', paragraphs: 21 },
        { marker: 'A.', paragraphs: 20 },
      ],
    }));
    confirm = mock(async () => true);
    promptText = mock(async () => null);
    toasts = [];
    paragraphs.set({});
    override(BookApi, {
      bulkAssignPreview,
      releaseNarrationPreview,
    } as unknown as BookApi);
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
        folder: signal('dracula'),
        speakers: signal({ names: { h: 'Elizabeth' }, narrator: null }),
        overview: signal({
          characters: [{ id: 'h', name: 'Elizabeth', aliases: [{ id: 'a', name: 'Lizzy' }] }],
        }),
        chapters,
      } as unknown as BookStore,
      { paragraphs } as unknown as ProjectStore,
    );
  });

  it('an item pick posts SetItemCharacter, then forgets the paragraph outcome', async () => {
    await assigner.assign({ kind: 'item', itemId: 'i2', paragraphId: 'p1' }, 'h');
    expect(run).toHaveBeenCalledWith({ type: 'SetItemCharacter', itemId: 'i2', characterId: 'h' });
    expect(clearOutcome).toHaveBeenCalledWith('dracula', 'p1');
    // The line had no audio to lose: no permanent-change confirm.
    expect(confirm).not.toHaveBeenCalled();
  });

  it('an item pick that discards audio asks first; declined, it writes nothing', async () => {
    confirm.mockResolvedValueOnce(false);
    await assigner.assign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, 'g');
    expect(confirm).toHaveBeenCalledWith({
      title: 'Assign?',
      message: 'Discards audio on 1 item. It must be generated again for the new speaker.',
      confirmLabel: 'Assign',
      destructive: true,
    });
    expect(run).not.toHaveBeenCalled();

    await assigner.assign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, 'g');
    expect(run).toHaveBeenCalledWith({ type: 'SetItemCharacter', itemId: 'i1', characterId: 'g' });
  });

  it('a pick onto the speaker the item already has keeps its audio and asks nothing', async () => {
    await assigner.assign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, 'h');
    expect(confirm).not.toHaveBeenCalled();
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('a paragraph clear counts the audio on the dialog lines it sweeps, not the narration', async () => {
    await assigner.assign({ kind: 'paragraph', paragraphId: 'p1' }, null);
    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Clear?',
        message: expect.stringContaining('Discards audio on 1 item.'),
        destructive: true,
      }),
    );
    expect(run).toHaveBeenCalledTimes(1);
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

  it('new character: one CreateCharacterAndAssign names the item, then forgets the outcome', async () => {
    await assigner.createAndAssign({ kind: 'item', itemId: 'i2', paragraphId: 'p1' }, '  Jane ');
    expect(run.mock.calls).toEqual([
      [{ type: 'CreateCharacterAndAssign', name: 'Jane', itemId: 'i2' }],
    ]);
    expect(execute).not.toHaveBeenCalled();
    expect(clearOutcome).toHaveBeenCalledWith('dracula', 'p1');
    // i2 had no audio to lose.
    expect(confirm).not.toHaveBeenCalled();
  });

  it('new character onto voiced lines asks about the audio before anything is written', async () => {
    confirm.mockResolvedValueOnce(false);
    await assigner.createAndAssign({ kind: 'paragraph', paragraphId: 'p1' }, 'Jane');
    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({ message: expect.stringContaining('Discards audio on 1 item.') }),
    );
    expect(run).not.toHaveBeenCalled();
  });

  it('new character named like a roster speaker or alias keeps its audio and asks nothing', async () => {
    await assigner.createAndAssign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, 'elizabeth');
    await assigner.createAndAssign({ kind: 'item', itemId: 'i1', paragraphId: 'p1' }, 'lizzy');
    expect(confirm).not.toHaveBeenCalled();
    expect(run).toHaveBeenCalledWith({
      type: 'CreateCharacterAndAssign',
      name: 'elizabeth',
      itemId: 'i1',
    });
  });

  it('new character with a blank search asks for the name, then sends one command', async () => {
    promptText.mockResolvedValueOnce('Jane');
    await assigner.createAndAssign({ kind: 'item', itemId: 'i2', paragraphId: 'p1' }, '  ');
    expect(promptText).toHaveBeenCalledTimes(1);
    expect(run.mock.calls).toEqual([
      [{ type: 'CreateCharacterAndAssign', name: 'Jane', itemId: 'i2' }],
    ]);
  });

  it('new character: a cancelled name prompt writes nothing', async () => {
    await assigner.createAndAssign({ kind: 'paragraph', paragraphId: 'p1' }, '');
    expect(promptText).toHaveBeenCalledTimes(1);
    expect(run).not.toHaveBeenCalled();
  });

  it('new character: a refused write forgets no outcome', async () => {
    run.mockResolvedValueOnce(false);
    await assigner.createAndAssign({ kind: 'item', itemId: 'i2', paragraphId: 'p1' }, 'Jane');
    expect(clearOutcome).not.toHaveBeenCalled();
  });

  it('new character over a selection previews, confirms with the typed name, then writes once', async () => {
    await assigner.createAndAssign({ kind: 'selection', paragraphIds: ['p1', 'p2'] }, 'Jane');
    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Assign Jane to selection' }),
    );
    expect(run.mock.calls).toEqual([
      [{ type: 'CreateCharacterAndAssign', name: 'Jane', paragraphIds: ['p1', 'p2'] }],
    ]);
    expect(toasts).toEqual([
      { kind: 'success', message: 'Assigned Jane to 3 lines in 2 paragraphs.' },
    ]);
  });

  it('bulk: previews, confirms with the wording, writes once and clears only remembered outcomes', async () => {
    paragraphs.set({ p1: { outcome: { kind: 'Failed' } }, p2: null });
    await assigner.assign({ kind: 'selection', paragraphIds: ['p1', 'p2', 'p3'] }, 'h');

    expect(bulkAssignPreview).toHaveBeenCalledWith('dracula', ['p1', 'p2', 'p3']);
    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Assign Elizabeth to selection',
        message:
          'Elizabeth becomes the speaker for 3 dialog lines in 2 paragraphs. Existing speakers are ' +
          'replaced. 1 selected paragraph have no dialog and stay unchanged.',
      }),
    );
    expect(run).toHaveBeenCalledWith({
      type: 'SetParagraphsCharacter',
      paragraphIds: ['p1', 'p2', 'p3'],
      characterId: 'h',
    });
    expect(clearOutcome.mock.calls).toEqual([['dracula', 'p1']]);
    expect(toasts).toEqual([
      { kind: 'success', message: 'Assigned Elizabeth to 3 lines in 2 paragraphs.' },
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

  it('release: previews the scope, confirms with the count, markers and audio, then writes once', async () => {
    const released = await assigner.releaseNarration('Chapter', 'c6', 'Chapter 6');

    expect(released).toBe(true);
    expect(releaseNarrationPreview).toHaveBeenCalledWith('dracula', 'Chapter', 'c6');
    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({
        message:
          'Release 41 narration paragraphs in Chapter 6? Speech markers: Q. ×21, A. ×20. ' +
          '12 have audio, which will be discarded.',
      }),
    );
    expect(run.mock.calls).toEqual([
      [{ type: 'ReleaseNarration', scope: 'Chapter', nodeId: 'c6' }],
    ]);
    expect(toasts).toEqual([
      {
        kind: 'success',
        message: 'Released 41 paragraphs. Assign their speakers or attribute them.',
      },
    ]);
  });

  it('release: a part with nothing speech-like is a notice and sends no command', async () => {
    releaseNarrationPreview.mockResolvedValueOnce({
      paragraphs: 0,
      itemsWithAudio: 0,
      speechMarkers: [],
    });
    expect(await assigner.releaseNarration('Part', 'p1', 'Part 1')).toBe(false);
    expect(confirm).not.toHaveBeenCalled();
    expect(run).not.toHaveBeenCalled();
    expect(toasts).toEqual([
      { kind: 'info', message: 'No speech-like paragraphs here — nothing to release.' },
    ]);
  });

  it('release: one paragraph without audio is written with no confirm', async () => {
    releaseNarrationPreview.mockResolvedValueOnce({
      paragraphs: 1,
      itemsWithAudio: 0,
      speechMarkers: [],
    });
    expect(await assigner.releaseNarration('Paragraph', 'p7', 'Q: Your name?')).toBe(true);
    expect(releaseNarrationPreview).toHaveBeenCalledWith('dracula', 'Paragraph', 'p7');
    expect(confirm).not.toHaveBeenCalled();
    expect(run.mock.calls).toEqual([
      [{ type: 'ReleaseNarration', scope: 'Paragraph', nodeId: 'p7' }],
    ]);
  });

  it('release: one paragraph with audio confirms first, naming the audio', async () => {
    releaseNarrationPreview.mockResolvedValueOnce({
      paragraphs: 1,
      itemsWithAudio: 1,
      speechMarkers: [],
    });
    confirm.mockResolvedValueOnce(false);
    expect(await assigner.releaseNarration('Paragraph', 'p7', 'Q: Your name?')).toBe(false);
    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({
        message: 'Release this narration paragraph? 1 item has audio, which will be discarded.',
      }),
    );
    expect(run).not.toHaveBeenCalled();
  });

  it('release: a declined confirm, a refused write or a failed preview releases nothing', async () => {
    confirm.mockResolvedValueOnce(false);
    expect(await assigner.releaseNarration('Chapter', 'c6', 'Chapter 6')).toBe(false);
    expect(run).not.toHaveBeenCalled();

    run.mockResolvedValueOnce(false);
    expect(await assigner.releaseNarration('Chapter', 'c6', 'Chapter 6')).toBe(false);

    releaseNarrationPreview.mockRejectedValueOnce(new ApiError(500, 'Boom', 'preview broke'));
    expect(await assigner.releaseNarration('Chapter', 'c6', 'Chapter 6')).toBe(false);
    expect(run).toHaveBeenCalledTimes(1);
    expect(toasts.map((t) => t.kind)).toEqual(['problem']);
  });
});
