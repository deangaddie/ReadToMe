import { beforeEach, describe, expect, it, mock } from 'bun:test';
import { ApiError, AudioApi } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { Preflight } from '@app/shared/preflight';
import { ToastService } from '@app/ui/toast';
import { AudioGenerator } from './audio-generator';
import type { BookEditor } from './book-editor';
import type { BookStore } from './book-store';

/** Ported from the Angular TestBed spec, case for case. */
describe('AudioGenerator', () => {
  let generator: AudioGenerator;
  let enqueueItems: ReturnType<typeof mock>;
  let enqueue: ReturnType<typeof mock>;
  let run: ReturnType<typeof mock>;
  let ensureReady: ReturnType<typeof mock>;
  let toasts: { kind: string; message: unknown }[];

  beforeEach(() => {
    resetServices();
    enqueueItems = mock(async () => ({ enqueued: 2 }));
    enqueue = mock(async () => ({ enqueued: 5 }));
    run = mock(async () => true);
    ensureReady = mock(async () => true);
    toasts = [];
    override(AudioApi, { enqueueItems, enqueue } as unknown as AudioApi);
    override(Preflight, { ensureReady } as unknown as Preflight);
    override(ToastService, {
      success: (m: string) => toasts.push({ kind: 'success', message: m }),
      problem: (p: unknown) => toasts.push({ kind: 'problem', message: p }),
    } as unknown as ToastService);
    generator = new AudioGenerator(
      { run } as unknown as BookEditor,
      { folder: signal('dune') } as unknown as BookStore,
    );
  });

  it('a selection runs the audio preflight, posts the ids and reports the count', async () => {
    expect(await generator.enqueueItems(['i1', 'i2'])).toBe(true);
    expect(ensureReady).toHaveBeenCalledWith('audio');
    expect(enqueueItems).toHaveBeenCalledWith('dune', ['i1', 'i2']);
    expect(toasts).toEqual([{ kind: 'success', message: 'Queued 2 items' }]);
    expect(generator.working()).toBe(false);
  });

  it('an empty selection and a cancelled preflight post nothing', async () => {
    expect(await generator.enqueueItems([])).toBe(false);
    ensureReady.mockResolvedValue(false);
    expect(await generator.retry('i1')).toBe(false);
    expect(enqueueItems).not.toHaveBeenCalled();
  });

  it('retry re-queues just that item', async () => {
    enqueueItems.mockResolvedValue({ enqueued: 1 });
    expect(await generator.retry('i9')).toBe(true);
    expect(enqueueItems).toHaveBeenCalledWith('dune', ['i9']);
    expect(toasts).toEqual([{ kind: 'success', message: 'Queued 1 item' }]);
  });

  it('a node asks for what still needs audio, with the narrator-only flag', async () => {
    expect(await generator.enqueueNode('chapter', 'c1', true)).toBe(true);
    expect(enqueue).toHaveBeenCalledWith('dune', {
      level: 'chapter',
      nodeId: 'c1',
      needsAudioOnly: true,
      narratorOnlyMode: true,
    });
  });

  it('nothing queued is said so and answers false; a 409 becomes a problem toast', async () => {
    enqueueItems.mockResolvedValue({ enqueued: 0 });
    expect(await generator.enqueueItems(['i1'])).toBe(false);
    expect(toasts).toEqual([{ kind: 'success', message: 'Nothing to queue' }]);

    toasts = [];
    enqueueItems.mockRejectedValue(
      new ApiError(409, 'Conflict', 'No paragraph TTS service configured.'),
    );
    expect(await generator.enqueueItems(['i1'])).toBe(false);
    expect(toasts[0]?.kind).toBe('problem');
    expect((toasts[0]!.message as { detail?: string }).detail).toContain('No paragraph TTS');
    expect(generator.working()).toBe(false);
  });

  it('dismissing a review posts the command through the editor', async () => {
    expect(await generator.dismissReview('i1')).toBe(true);
    expect(run).toHaveBeenCalledWith({ type: 'DismissAudioReview', paragraphItemId: 'i1' });
  });
});
