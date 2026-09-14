import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiError, BookApi, ProjectsApi } from '@app/api';
import { ToastService } from '@app/ui/toast/toast.service';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';

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

describe('BookEditor', () => {
  let store: FakeStore;
  let editor: BookEditor;
  let execute: ReturnType<typeof vi.fn>;
  let importFile: ReturnType<typeof vi.fn>;
  let importManually: ReturnType<typeof vi.fn>;
  let problems: unknown[];

  const flush = () => new Promise((r) => setTimeout(r, 0));

  beforeEach(() => {
    store = new FakeStore();
    execute = vi.fn().mockResolvedValue({ newEntityId: null });
    importFile = vi.fn().mockResolvedValue(undefined);
    importManually = vi.fn().mockResolvedValue(undefined);
    problems = [];
    TestBed.configureTestingModule({
      providers: [
        BookEditor,
        { provide: BookStore, useValue: store },
        { provide: BookApi, useValue: { execute } },
        { provide: ProjectsApi, useValue: { import: importFile, importManually } },
        { provide: ToastService, useValue: { problem: (p: unknown) => problems.push(p) } },
      ],
    });
    editor = TestBed.inject(BookEditor);
  });

  it('posts the command and is done once its own receipt arrives, without reloading', async () => {
    const run = editor.run({ type: 'AddPauses' });
    await flush();
    expect(execute).toHaveBeenCalledWith('dune', { type: 'AddPauses' });
    expect(editor.busy()).toBe(true);
    expect(editor.locked()).toBe(true);

    store.settle(true);
    await expect(run).resolves.toBe(true);
    expect(store.refreshed).toBe(0);
    expect(editor.busy()).toBe(false);
    expect(problems).toEqual([]);
  });

  it('execute answers with the host response, so a create can use the id it resolved', async () => {
    execute.mockResolvedValueOnce({ newEntityId: 'c-new' });
    const created = editor.execute({ type: 'CreateCharacter', name: 'Gaal' });
    await flush();
    store.settle(true);
    await expect(created).resolves.toEqual({ newEntityId: 'c-new' });

    // The import writes answer nothing, and a reread still reports plain success.
    importFile.mockResolvedValueOnce(undefined);
    const reread = editor.reread();
    await flush();
    store.settle(true);
    await expect(reread).resolves.toBe(true);
  });

  it('reloads by hand when no own receipt comes back in time', async () => {
    const run = editor.run({ type: 'AddPauses' });
    await flush();
    store.settle(false);
    await expect(run).resolves.toBe(true);
    expect(store.refreshed).toBe(1);
  });

  it('a refusal becomes a toast with the detail, cancels the wait and reloads nothing', async () => {
    execute.mockRejectedValueOnce(new ApiError(422, 'Unprocessable', 'Paragraph is queued.'));
    await expect(editor.run({ type: 'AddPauses' })).resolves.toBe(false);
    expect(problems).toEqual([expect.objectContaining({ status: 422, detail: 'Paragraph is queued.' })]);
    expect(store.cancelled).toBe(1);
    expect(store.refreshed).toBe(0);
    expect(editor.busy()).toBe(false);
  });

  it('is locked while stale even when idle, and refuses to overlap writes', async () => {
    store.stale.set('boom');
    expect(editor.locked()).toBe(true);
    store.stale.set(null);

    const first = editor.run({ type: 'AddPauses' });
    await flush();
    await expect(editor.run({ type: 'AddBookTitle' })).resolves.toBe(false);
    expect(execute).toHaveBeenCalledTimes(1);
    store.settle(true);
    await first;
  });

  it('reread and manual reread go through the same wait', async () => {
    const reread = editor.reread();
    await flush();
    expect(importFile).toHaveBeenCalledWith('dune', true);
    store.settle(true);
    await expect(reread).resolves.toBe(true);

    const request = {
      hasMultipleVolumes: false,
      hasMultipleParts: false,
      volume: null,
      part: null,
      chapter: { mode: 'Roman' as const },
    };
    const manual = editor.rereadManually(request);
    await flush();
    expect(importManually).toHaveBeenCalledWith('dune', request);
    store.settle(false);
    await expect(manual).resolves.toBe(true);
    expect(store.refreshed).toBe(1);
  });
});
