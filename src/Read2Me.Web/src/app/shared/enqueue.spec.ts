import { beforeEach, describe, expect, it, mock } from 'bun:test';
import { ApiError } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal, type WritableSignal } from '@app/core/signals';
import { Preflight } from '@app/shared/preflight';
import { ToastService } from '@app/ui/toast';
import { enqueueWithPreflight } from './enqueue';

describe('enqueueWithPreflight', () => {
  let busy: WritableSignal<boolean>;
  let ensureReady: ReturnType<typeof mock>;
  let request: ReturnType<typeof mock>;
  let toasts: { kind: string; message: unknown }[];

  beforeEach(() => {
    resetServices();
    busy = signal(false);
    ensureReady = mock(async () => true);
    request = mock(async () => ({ enqueued: 3 }));
    toasts = [];
    override(Preflight, { ensureReady } as unknown as Preflight);
    override(ToastService, {
      success: (m: string) => toasts.push({ kind: 'success', message: m }),
      problem: (p: unknown) => toasts.push({ kind: 'problem', message: p }),
    } as unknown as ToastService);
  });

  const run = () => enqueueWithPreflight({ busy, task: 'attribution', unit: 'paragraph', request });

  it('answers false at once while busy, with no preflight', async () => {
    busy.set(true);
    expect(await run()).toBe(false);
    expect(ensureReady).not.toHaveBeenCalled();
    expect(request).not.toHaveBeenCalled();
    expect(busy()).toBe(true);
  });

  it('runs the task preflight then the request, and toasts the count with the plural unit', async () => {
    let busyDuringRequest: boolean | undefined;
    request.mockImplementation(async () => {
      busyDuringRequest = busy();
      return { enqueued: 3 };
    });
    expect(await run()).toBe(true);
    expect(ensureReady).toHaveBeenCalledWith('attribution');
    expect(busyDuringRequest).toBe(true);
    expect(busy()).toBe(false);
    expect(toasts).toEqual([{ kind: 'success', message: 'Queued 3 paragraphs' }]);
  });

  it('one queued takes the singular unit', async () => {
    request.mockResolvedValue({ enqueued: 1 });
    expect(await run()).toBe(true);
    expect(toasts).toEqual([{ kind: 'success', message: 'Queued 1 paragraph' }]);
  });

  it('a cancelled preflight sends nothing and answers false', async () => {
    ensureReady.mockResolvedValue(false);
    expect(await run()).toBe(false);
    expect(request).not.toHaveBeenCalled();
    expect(toasts).toEqual([]);
    expect(busy()).toBe(false);
  });

  it('nothing queued is said so and answers false', async () => {
    request.mockResolvedValue({ enqueued: 0 });
    expect(await run()).toBe(false);
    expect(toasts).toEqual([{ kind: 'success', message: 'Nothing to queue' }]);
  });

  it('a thrown error becomes a problem toast and answers false', async () => {
    request.mockRejectedValue(
      new ApiError(409, 'Conflict', 'No paragraph TTS service configured.'),
    );
    expect(await run()).toBe(false);
    expect(toasts[0]?.kind).toBe('problem');
    expect((toasts[0]!.message as { detail?: string }).detail).toContain('No paragraph TTS');
    expect(busy()).toBe(false);
  });
});
