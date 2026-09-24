import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiError, AudioApi } from '@app/api';
import { Preflight } from '@app/shared/preflight';
import { ToastService } from '@app/ui/toast/toast.service';
import { AudioGenerator } from './audio-generator';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';

describe('AudioGenerator', () => {
  let generator: AudioGenerator;
  let enqueueItems: ReturnType<typeof vi.fn>;
  let enqueue: ReturnType<typeof vi.fn>;
  let run: ReturnType<typeof vi.fn>;
  let ensureReady: ReturnType<typeof vi.fn>;
  let toasts: { kind: string; message: unknown }[];

  beforeEach(() => {
    enqueueItems = vi.fn().mockResolvedValue({ enqueued: 2 });
    enqueue = vi.fn().mockResolvedValue({ enqueued: 5 });
    run = vi.fn().mockResolvedValue(true);
    ensureReady = vi.fn().mockResolvedValue(true);
    toasts = [];
    TestBed.configureTestingModule({
      providers: [
        AudioGenerator,
        { provide: AudioApi, useValue: { enqueueItems, enqueue } },
        { provide: BookEditor, useValue: { run } },
        { provide: BookStore, useValue: { folder: signal('dune') } },
        { provide: Preflight, useValue: { ensureReady } },
        {
          provide: ToastService,
          useValue: {
            success: (m: string) => toasts.push({ kind: 'success', message: m }),
            problem: (p: unknown) => toasts.push({ kind: 'problem', message: p }),
          },
        },
      ],
    });
    generator = TestBed.inject(AudioGenerator);
  });

  it('a selection runs the audio preflight, posts the ids and reports the count', async () => {
    await expect(generator.enqueueItems(['i1', 'i2'])).resolves.toBe(true);
    expect(ensureReady).toHaveBeenCalledWith('audio');
    expect(enqueueItems).toHaveBeenCalledWith('dune', ['i1', 'i2']);
    expect(toasts).toEqual([{ kind: 'success', message: 'Queued 2 items' }]);
    expect(generator.working()).toBe(false);
  });

  it('an empty selection and a cancelled preflight post nothing', async () => {
    await expect(generator.enqueueItems([])).resolves.toBe(false);
    ensureReady.mockResolvedValue(false);
    await expect(generator.retry('i1')).resolves.toBe(false);
    expect(enqueueItems).not.toHaveBeenCalled();
  });

  it('retry re-queues just that item', async () => {
    enqueueItems.mockResolvedValue({ enqueued: 1 });
    await expect(generator.retry('i9')).resolves.toBe(true);
    expect(enqueueItems).toHaveBeenCalledWith('dune', ['i9']);
    expect(toasts).toEqual([{ kind: 'success', message: 'Queued 1 item' }]);
  });

  it('a node asks for what still needs audio, with the narrator-only flag', async () => {
    await expect(generator.enqueueNode('chapter', 'c1', true)).resolves.toBe(true);
    expect(enqueue).toHaveBeenCalledWith('dune', {
      level: 'chapter',
      nodeId: 'c1',
      needsAudioOnly: true,
      narratorOnlyMode: true,
    });
  });

  it('nothing queued is said so and answers false; a 409 becomes a problem toast', async () => {
    enqueueItems.mockResolvedValue({ enqueued: 0 });
    await expect(generator.enqueueItems(['i1'])).resolves.toBe(false);
    expect(toasts).toEqual([{ kind: 'success', message: 'Nothing to queue' }]);

    toasts = [];
    enqueueItems.mockRejectedValue(new ApiError(409, 'Conflict', 'No paragraph TTS service configured.'));
    await expect(generator.enqueueItems(['i1'])).resolves.toBe(false);
    expect(toasts[0]?.kind).toBe('problem');
    expect((toasts[0]?.message as { detail?: string }).detail).toContain('No paragraph TTS');
    expect(generator.working()).toBe(false);
  });

  it('dismissing a review posts the command through the editor', async () => {
    await expect(generator.dismissReview('i1')).resolves.toBe(true);
    expect(run).toHaveBeenCalledWith({ type: 'DismissAudioReview', paragraphItemId: 'i1' });
  });
});
