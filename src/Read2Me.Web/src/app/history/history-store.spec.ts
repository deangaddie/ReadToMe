import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { BookApi, type HistoryDto, type HistoryStep, StepView } from '@app/api';
import { override, resetServices, use } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../testing/fake-api';
import { FakeLive } from '../../testing/fake-live';
import { HistoryStore } from './history-store';

const PATH = '/api/projects/dune/history';

function step(sequence: number, overrides: Partial<HistoryStep> = {}): HistoryStep {
  return {
    stepId: `s${sequence}`,
    parentStepId: sequence > 1 ? `s${sequence - 1}` : null,
    sequence,
    label: 'Edit text',
    summary: null,
    author: 'dean',
    startedAt: '2026-10-20T09:00:00Z',
    endedAt: '2026-10-20T09:00:00Z',
    isOpen: false,
    note: null,
    restoredFromStepId: null,
    cantOpen: false,
    ...overrides,
  };
}

function dto(steps: HistoryStep[]): HistoryDto {
  return { steps, headStepId: steps[0]?.stepId ?? null, busy: null, restoring: false };
}

describe('HistoryStore', () => {
  let api: FakeApi;
  let live: FakeLive;
  let store: HistoryStore;
  const toasts: string[] = [];

  beforeEach(() => {
    api = new FakeApi();
    api.install();
    live = new FakeLive().install();
    toasts.length = 0;
    override(ToastService, { info: (m: string) => toasts.push(m) } as unknown as ToastService);
    store = new HistoryStore();
    store.open('dune');
  });

  afterEach(() => {
    store.close();
    resetServices();
  });

  const landed = (s: HistoryStep, folder = 'dune') =>
    live.emit('history', { kind: 'stepLanded', folder, step: s });
  const closed = (s: HistoryStep, folder = 'dune') =>
    live.emit('history', { kind: 'stepClosed', folder, step: s });
  const ids = () => store.steps().map((s) => s.stepId);

  it('loads the steps newest first with the Head, in one request however long History is', async () => {
    const steps = Array.from({ length: 300 }, (_, i) => step(300 - i));
    api.on('GET', PATH, dto(steps));

    await store.load();

    expect(store.steps()).toHaveLength(300);
    expect(store.headStepId()).toBe('s300');
    expect(api.requests).toHaveLength(1);
  });

  it('loads once: opening the sheet again reads the live list, not the API', async () => {
    api.on('GET', PATH, dto([step(1)]));

    await store.load();
    await store.load();

    expect(api.calls('GET', PATH)).toHaveLength(1);
  });

  it('puts a landed step on top and moves the Head onto it', async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    landed(step(2, { isOpen: true }));

    expect(ids()).toEqual(['s2', 's1']);
    expect(store.headStepId()).toBe('s2');
    expect(store.steps()[0]?.isOpen).toBe(true);
  });

  it('replaces a step by id when it closes', async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    landed(step(2, { isOpen: true }));
    closed(step(2, { endedAt: '2026-10-20T09:20:00Z' }));

    expect(ids()).toEqual(['s2', 's1']);
    expect(store.steps()[0]).toMatchObject({ isOpen: false, endedAt: '2026-10-20T09:20:00Z' });
  });

  it('replaces an open step a later commit extended, as its span and label move on', async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    landed(step(2, { label: 'Attribution', isOpen: true }));
    live.emit('history', {
      kind: 'stepChanged',
      folder: 'dune',
      step: step(2, {
        label: 'Attribution and voice batch',
        isOpen: true,
        endedAt: '2026-10-20T09:20:00Z',
      }),
    });

    expect(ids()).toEqual(['s2', 's1']);
    expect(store.steps()[0]).toMatchObject({
      label: 'Attribution and voice batch',
      endedAt: '2026-10-20T09:20:00Z',
    });
  });

  it('replaces a closed step sent again with a note, as "part 1" lands once part 2 starts', async () => {
    api.on('GET', PATH, dto([step(2, { label: 'Edit text' }), step(1, { label: 'Attribution' })]));
    await store.load();

    closed(step(1, { label: 'Attribution', note: 'part 1' }));

    expect(ids()).toEqual(['s2', 's1']);
    expect(store.steps()[1]?.note).toBe('part 1');
    expect(store.headStepId()).toBe('s2');
  });

  it('reads a hub row with its nulls omitted as null', async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    const {
      note: _note,
      summary: _summary,
      restoredFromStepId: _from,
      endedAt: _end,
      ...omitted
    } = step(2);
    live.emit('history', { kind: 'stepLanded', folder: 'dune', step: omitted });

    expect(store.steps()[0]).toEqual(
      step(2, { note: null, summary: null, restoredFromStepId: null, endedAt: null }),
    );
  });

  it("ignores another project's steps", async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    landed(step(2), 'arrakis');

    expect(ids()).toEqual(['s1']);
  });

  it('takes the Restore step from its landing, not from the restoring messages around it', async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    live.emit('history', { kind: 'restoring', folder: 'dune', phase: 'started' });
    landed(step(2));
    live.emit('history', { kind: 'restoring', folder: 'dune', phase: 'finished', step: step(2) });

    expect(ids()).toEqual(['s2', 's1']);
  });

  it('keeps a step that lands while the list is still loading', async () => {
    let answer!: (value: HistoryDto) => void;
    api.on('GET', PATH, () => new Promise<HistoryDto>((resolve) => (answer = resolve)));
    const loading = store.load();

    landed(step(2, { isOpen: true }));
    answer(dto([step(1)]));
    await loading;

    expect(ids()).toEqual(['s2', 's1']);
    expect(store.headStepId()).toBe('s2');
  });

  it('does not reopen a step the list already has closed', async () => {
    let answer!: (value: HistoryDto) => void;
    api.on('GET', PATH, () => new Promise<HistoryDto>((resolve) => (answer = resolve)));
    const loading = store.load();

    landed(step(1, { isOpen: true }));
    answer(dto([step(1)]));
    await loading;

    expect(store.steps()[0]?.isOpen).toBe(false);
  });

  it('rereads the list after the hub reconnects, once it has loaded', async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();
    api.on('GET', PATH, dto([step(2), step(1)]));

    live.resync();
    await store.settled();

    expect(ids()).toEqual(['s2', 's1']);
  });

  it('reads nothing on a reconnect before the sheet has asked for it', async () => {
    live.resync();
    await store.settled();

    expect(api.requests).toHaveLength(0);
  });

  it('reports a failed read', async () => {
    api.on('GET', PATH, () => Response.json({ status: 500, detail: 'boom' }, { status: 500 }));

    await store.load();

    expect(store.error()).toBe('boom');
    expect(store.loaded()).toBe(false);
  });

  describe('viewing a step', () => {
    const BOOK = '/api/projects/dune/book';
    const readBook = async () => {
      await use(BookApi).overview('dune');
      return api.calls('GET', BOOK).at(-1)?.search;
    };
    const busyChanged = (reason?: string, folder = 'dune') =>
      live.emit('history', { kind: 'busyChanged', folder, ...(reason ? { reason } : {}) });

    beforeEach(async () => {
      api.on('GET', BOOK, {});
      api.on('GET', PATH, dto([step(3, { label: 'Assign speaker' }), step(2), step(1)]));
      await store.load();
    });

    it('starts on the current project, reading without ?at=', async () => {
      expect(store.viewing()).toBeNull();
      expect(await readBook()).toBe('');
    });

    it('reads every Project state read at the viewed step, and names that step', async () => {
      expect(store.view('s2')).toBe(true);

      expect(store.viewing()).toBe('s2');
      expect(store.viewedStep()?.stepId).toBe('s2');
      expect(await readBook()).toBe('?at=s2');
    });

    it('follows the viewed row as the hub replaces it by id', () => {
      store.view('s2');
      closed(step(2, { note: 'part 1' }));

      expect(store.viewedStep()?.note).toBe('part 1');
    });

    it('returns to the current project on Back to current', async () => {
      store.view('s2');
      store.backToCurrent();

      expect(store.viewing()).toBeNull();
      expect(await readBook()).toBe('');
    });

    it('returns to the current project when the tip is clicked', async () => {
      store.view('s2');
      store.view('s3');

      expect(store.viewing()).toBeNull();
      expect(await readBook()).toBe('');
    });

    it("can't view a step while the project is busy, and says why", async () => {
      busyChanged('Attribution: 128 paragraphs left');

      expect(store.busy()).toBe('Attribution: 128 paragraphs left');
      expect(store.view('s2')).toBe(false);
      expect(store.viewing()).toBeNull();
      expect(await readBook()).toBe('');
    });

    it('reads the busy reason with the list', async () => {
      store.close();
      store.open('dune');
      api.on('GET', PATH, { ...dto([step(1)]), busy: { reason: 'Assembly running' } });
      await store.load();

      expect(store.busy()).toBe('Assembly running');
    });

    it('sends a viewing tab back to current with a toast when background work starts', async () => {
      store.view('s2');
      busyChanged('Attribution: 128 paragraphs left');

      expect(store.viewing()).toBeNull();
      expect(await readBook()).toBe('');
      expect(toasts).toEqual([
        'Back to the current project: background work started (Attribution: 128 paragraphs left).',
      ]);
    });

    it('reads busyChanged without a reason as idle, so steps can be viewed again', () => {
      busyChanged('Voice batch running');
      busyChanged();

      expect(store.busy()).toBeNull();
      expect(store.view('s2')).toBe(true);
      expect(toasts).toEqual([]);
    });

    it("ignores another project's busy change", () => {
      store.view('s2');
      busyChanged('Voice batch running', 'arrakis');

      expect(store.viewing()).toBe('s2');
      expect(store.busy()).toBeNull();
    });

    it('stops viewing when the project closes', async () => {
      store.view('s2');
      store.close();

      expect(use(StepView).at('dune')).toBeNull();
      expect(await readBook()).toBe('');
    });
  });
});
