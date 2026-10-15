import { beforeEach, describe, expect, it } from 'bun:test';
import { ApiError, BookApi, ProjectsApi } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { ToastService } from '@app/ui/toast';
import { BookEditor } from './book-editor';
import type { BookStore } from './book-store';

/** Ported from the Angular TestBed spec, case for case; the fakes go in through `override()`. */
class FakeStore {
  readonly folder = signal<string | null>('dune');
  readonly stale = signal<string | null>(null);
  refreshed = 0;
  waiters: ((hit: boolean) => void)[] = [];
  cancelled = 0;

  expectOwnReceipt() {
    const settled = new Promise<boolean>((resolve) => this.waiters.push(resolve));
    return { settled, cancel: () => this.cancelled++ };
  }

  async refresh() {
    this.refreshed++;
  }

  /** The hub echoed this tab's receipt (true) or the timeout elapsed (false). */
  settle(hit: boolean) {
    for (const w of this.waiters.splice(0)) w(hit);
  }
}

/** A recording stand-in for one API method: scripted answers, calls kept. */
function fn<R>(answer: () => Promise<R>) {
  const calls: unknown[][] = [];
  let next: (() => Promise<R>) | null = null;
  const call = (...args: unknown[]) => {
    calls.push(args);
    const run = next ?? answer;
    next = null;
    return run();
  };
  return { call, calls, once: (run: () => Promise<R>) => (next = run) };
}

describe('BookEditor', () => {
  let store: FakeStore;
  let editor: BookEditor;
  let execute: ReturnType<typeof fn<{ newEntityId: string | null }>>;
  let importFile: ReturnType<typeof fn<void>>;
  let importManually: ReturnType<typeof fn<void>>;
  let problems: unknown[];

  const flush = () => new Promise((r) => setTimeout(r, 0));

  beforeEach(() => {
    resetServices();
    store = new FakeStore();
    execute = fn<{ newEntityId: string | null }>(async () => ({ newEntityId: null }));
    importFile = fn<void>(async () => {});
    importManually = fn<void>(async () => {});
    problems = [];
    override(BookApi, { execute: execute.call } as unknown as BookApi);
    override(ProjectsApi, {
      import: importFile.call,
      importManually: importManually.call,
    } as unknown as ProjectsApi);
    override(ToastService, { problem: (p: unknown) => problems.push(p) } as unknown as ToastService);
    editor = new BookEditor(store as unknown as BookStore);
  });

  it('posts the command and is done once its own receipt arrives, without reloading', async () => {
    const run = editor.run({ type: 'AddPauses' });
    await flush();
    expect(execute.calls).toEqual([['dune', { type: 'AddPauses' }]]);
    expect(editor.busy()).toBe(true);
    expect(editor.locked()).toBe(true);

    store.settle(true);
    expect(await run).toBe(true);
    expect(store.refreshed).toBe(0);
    expect(editor.busy()).toBe(false);
    expect(problems).toEqual([]);
  });

  it('execute answers with the host response, so a create can use the id it resolved', async () => {
    execute.once(async () => ({ newEntityId: 'c-new' }));
    const created = editor.execute({ type: 'CreateCharacter', name: 'Gaal' });
    await flush();
    store.settle(true);
    expect(await created).toEqual({ newEntityId: 'c-new' });

    // The import writes answer nothing, and a reread still reports plain success.
    const reread = editor.reread();
    await flush();
    store.settle(true);
    expect(await reread).toBe(true);
  });

  it('reloads by hand when no own receipt comes back in time', async () => {
    const run = editor.run({ type: 'AddPauses' });
    await flush();
    store.settle(false);
    expect(await run).toBe(true);
    expect(store.refreshed).toBe(1);
  });

  it('a refusal becomes a toast with the detail, cancels the wait and reloads nothing', async () => {
    execute.once(() => Promise.reject(new ApiError(422, 'Unprocessable', 'Paragraph is queued.')));
    expect(await editor.run({ type: 'AddPauses' })).toBe(false);
    expect(problems).toEqual([
      expect.objectContaining({ status: 422, detail: 'Paragraph is queued.' }),
    ]);
    expect(store.cancelled).toBe(1);
    expect(store.refreshed).toBe(0);
    expect(editor.busy()).toBe(false);
  });

  it('tryExecute hands the refusal back instead of toasting, and explains a declined write', async () => {
    execute.once(() => Promise.reject(new ApiError(422, 'Unprocessable', 'Paragraph is queued.')));
    const refused = await editor.tryExecute({ type: 'AddPauses' });
    expect(refused?.detail).toBe('Paragraph is queued.');
    expect(problems).toEqual([]);

    const first = editor.run({ type: 'AddPauses' });
    await flush();
    const declined = await editor.tryExecute({ type: 'AddBookTitle' });
    expect(declined?.message).toBe('Another change is still being saved.');
    store.settle(true);
    await first;
  });

  it('is locked while stale even when idle, and refuses to overlap writes', async () => {
    store.stale.set('boom');
    expect(editor.locked()).toBe(true);
    store.stale.set(null);

    const first = editor.run({ type: 'AddPauses' });
    await flush();
    expect(await editor.run({ type: 'AddBookTitle' })).toBe(false);
    expect(execute.calls.length).toBe(1);
    store.settle(true);
    await first;
  });

  it('reread and manual reread go through the same wait', async () => {
    const reread = editor.reread();
    await flush();
    expect(importFile.calls).toEqual([['dune', true]]);
    store.settle(true);
    expect(await reread).toBe(true);

    const request = {
      hasMultipleVolumes: false,
      hasMultipleParts: false,
      volume: null,
      part: null,
      chapter: { mode: 'Roman' as const },
    };
    const manual = editor.rereadManually(request);
    await flush();
    expect(importManually.calls).toEqual([['dune', request]]);
    store.settle(false);
    expect(await manual).toBe(true);
    expect(store.refreshed).toBe(1);
  });
});
