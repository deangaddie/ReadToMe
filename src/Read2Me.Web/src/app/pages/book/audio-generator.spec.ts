import { beforeEach, describe, expect, it, mock } from 'bun:test';
import { AudioApi } from '@app/api';
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
      { folder: signal('dracula') } as unknown as BookStore,
    );
  });

  it('a selection runs the audio preflight, posts the ids and reports the count', async () => {
    expect(await generator.enqueueItems(['i1', 'i2'])).toBe(true);
    expect(ensureReady).toHaveBeenCalledWith('audio');
    expect(enqueueItems).toHaveBeenCalledWith('dracula', ['i1', 'i2']);
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
    expect(enqueueItems).toHaveBeenCalledWith('dracula', ['i9']);
    expect(toasts).toEqual([{ kind: 'success', message: 'Queued 1 item' }]);
  });

  it('a node asks for what still needs audio, with the narrator-only flag', async () => {
    expect(await generator.enqueueNode('Chapter', 'c1', true)).toBe(true);
    expect(enqueue).toHaveBeenCalledWith('dracula', {
      level: 'Chapter',
      nodeId: 'c1',
      needsAudioOnly: true,
      narratorOnlyMode: true,
    });
  });

  it('is working while a request is in flight, and a second request then posts nothing', async () => {
    let release!: (r: { enqueued: number }) => void;
    enqueueItems.mockImplementation(() => new Promise((r) => (release = r)));
    const first = generator.enqueueItems(['i1']);
    await Promise.resolve();
    expect(generator.working()).toBe(true);
    expect(await generator.retry('i2')).toBe(false);
    release({ enqueued: 1 });
    expect(await first).toBe(true);
    expect(enqueueItems).toHaveBeenCalledTimes(1);
    expect(generator.working()).toBe(false);
  });

  it('posts nothing without a project folder', async () => {
    generator = new AudioGenerator(
      { run } as unknown as BookEditor,
      { folder: signal(null) } as unknown as BookStore,
    );
    expect(await generator.enqueueNode('Chapter', 'c1', false)).toBe(false);
    expect(ensureReady).not.toHaveBeenCalled();
  });

  it('dismissing a review posts the command through the editor', async () => {
    expect(await generator.dismissReview('i1')).toBe(true);
    expect(run).toHaveBeenCalledWith({ type: 'DismissAudioReview', paragraphItemId: 'i1' });
  });
});
