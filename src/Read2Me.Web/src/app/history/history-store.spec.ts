import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { BookApi, type HistoryDto, type HistoryStep, StepView } from '@app/api';
import { Router } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';
import { type ProblemLike, type ToastAction, ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../testing/fake-api';
import { FakeLive } from '../../testing/fake-live';
import { HistoryStore } from './history-store';

const PATH = '/api/projects/dracula/history';

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
    kind: 'Request',
    action: 'Edit',
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
  /** The actions of the last toast that offered any. */
  let actions: ToastAction[] = [];
  const errors: string[] = [];
  const navigated: string[] = [];

  beforeEach(() => {
    api = new FakeApi();
    api.install();
    live = new FakeLive().install();
    toasts.length = 0;
    actions = [];
    errors.length = 0;
    navigated.length = 0;
    override(ToastService, {
      info: (m: string, offered?: ToastAction[]) => {
        toasts.push(m);
        if (offered) actions = offered;
      },
      problem: (p: ProblemLike) => errors.push(p.detail ?? ''),
    } as unknown as ToastService);
    override(Router, { navigate: (path: string) => navigated.push(path) } as unknown as Router);
    store = new HistoryStore();
    store.open('dracula');
  });

  afterEach(() => {
    store.close();
    resetServices();
  });

  const landed = (s: HistoryStep, folder = 'dracula') =>
    live.emit('history', { kind: 'stepLanded', folder, step: s });
  const closed = (s: HistoryStep, folder = 'dracula') =>
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
      folder: 'dracula',
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
      action: _action,
      restoredFromStepId: _from,
      endedAt: _end,
      ...omitted
    } = step(2);
    live.emit('history', { kind: 'stepLanded', folder: 'dracula', step: omitted });

    expect(store.steps()[0]).toEqual(
      step(2, { note: null, summary: null, action: null, restoredFromStepId: null, endedAt: null }),
    );
  });

  it("ignores another project's steps", async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    landed(step(2), 'alices-adventures-in-wonderland');

    expect(ids()).toEqual(['s1']);
  });

  it('takes the Restore step from its landing, not from the restoring messages around it', async () => {
    api.on('GET', PATH, dto([step(1)]));
    await store.load();

    live.emit('history', { kind: 'restoring', folder: 'dracula', phase: 'started' });
    landed(step(2));
    live.emit('history', {
      kind: 'restoring',
      folder: 'dracula',
      phase: 'finished',
      step: step(2),
    });

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
    const BOOK = '/api/projects/dracula/book';
    const readBook = async () => {
      await use(BookApi).overview('dracula');
      return api.calls('GET', BOOK).at(-1)?.search;
    };
    const busyChanged = (reason?: string, folder = 'dracula') =>
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
      store.open('dracula');
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
      busyChanged('Voice batch running', 'alices-adventures-in-wonderland');

      expect(store.viewing()).toBe('s2');
      expect(store.busy()).toBeNull();
    });

    it('stops viewing when the project closes', async () => {
      store.view('s2');
      store.close();

      expect(use(StepView).at('dracula')).toBeNull();
      expect(await readBook()).toBe('');
    });
  });

  describe('Restore', () => {
    const STEP2 = `${PATH}/s2`;
    const RESTORE2 = `${STEP2}/restore`;
    const restoreStep = (sequence: number, from: string) =>
      step(sequence, { label: `Restore to 'Edit text' (09:00)`, restoredFromStepId: from });
    const restoring = (phase: 'started' | 'finished', s?: HistoryStep, folder = 'dracula') =>
      live.emit('history', { kind: 'restoring', folder, phase, ...(s ? { step: s } : {}) });

    beforeEach(async () => {
      api.on('GET', PATH, dto([step(3), step(2), step(1)]));
      api.on('GET', STEP2, { ...step(2), restorePreview: { audioDiscardCount: 60 } });
      api.on('POST', RESTORE2, {
        step: restoreStep(4, 's2'),
        audioDiscardCount: 60,
        needsAudioCount: 75,
      });
      await store.load();
    });

    it('reads what a Restore would cost when a step is viewed, and forgets it on Back to current', async () => {
      store.view('s2');
      expect(store.restorePreview()).toBeNull();
      await store.settled();

      expect(store.restorePreview()).toBe(60);

      store.backToCurrent();
      expect(store.restorePreview()).toBeNull();
    });

    it('says to try again when the step could not be opened just now, and forgets it on viewing again', async () => {
      api.on('GET', STEP2, () =>
        problem(503, "History step s2 couldn't be opened just now. Try again."),
      );
      store.view('s2');
      await store.settled();

      expect(store.viewError()).toBe("Couldn't open this step just now. Try again.");
      expect(store.restorePreview()).toBeNull();

      api.on('GET', STEP2, { ...step(2), restorePreview: { audioDiscardCount: 60 } });
      store.view('s2');
      expect(store.viewError()).toBeNull();
      await store.settled();
      expect(store.restorePreview()).toBe(60);
    });

    it("says a step can't be opened when opening it marks it", async () => {
      api.on('GET', STEP2, { ...step(2, { cantOpen: true }), restorePreview: null });
      store.view('s2');
      await store.settled();

      expect(store.viewError()).toBe("This step can't be opened.");
    });

    it('restores the viewed step: back to current, the new step at the tip, and a toast with Show in Book and Undo restore', async () => {
      store.view('s2');
      await store.settled();

      const done = store.restore();
      restoring('started');
      expect(store.restoring()).toBe(true);
      await done;
      landed(restoreStep(4, 's2'));
      restoring('finished', restoreStep(4, 's2'));

      expect(api.calls('POST', RESTORE2)).toHaveLength(1);
      expect(store.viewing()).toBeNull();
      expect(store.restoring()).toBe(false);
      expect(store.headStepId()).toBe('s4');
      expect(toasts).toEqual([
        'Restored to #2 Edit text. The later steps stay in History; 75 items need generating.',
      ]);
      expect(actions.map((a) => a.label)).toEqual(['Show in Book', 'Undo restore']);
    });

    it('Show in Book goes to the Book page', async () => {
      store.view('s2');
      await store.restore();

      actions.find((a) => a.label === 'Show in Book')?.run();

      expect(navigated).toEqual(['projects/dracula/book']);
    });

    it('Undo restore restores the step that was the tip before', async () => {
      api.on('POST', `${PATH}/s3/restore`, {
        step: restoreStep(5, 's3'),
        audioDiscardCount: 2,
        needsAudioCount: 1,
      });
      store.view('s2');
      await store.restore();
      landed(restoreStep(4, 's2'));

      actions.find((a) => a.label === 'Undo restore')?.run();
      await store.settled();

      expect(api.calls('POST', `${PATH}/s3/restore`)).toHaveLength(1);
      expect(toasts.at(-1)).toBe(
        'Restored to #3 Edit text. The later steps stay in History; 1 item needs generating.',
      );
    });

    it('Undo restore takes the tip before from the answer, with no list read', async () => {
      const unread = new HistoryStore();
      unread.open('dracula');
      api.on('POST', `${PATH}/s3/restore`, {
        step: restoreStep(5, 's3'),
        audioDiscardCount: 0,
        needsAudioCount: 0,
      });
      const reads = api.calls('GET', PATH).length;

      await unread.restore('s2');
      actions.find((a) => a.label === 'Undo restore')?.run();
      await unread.settled();
      unread.close();

      expect(api.calls('GET', PATH)).toHaveLength(reads);
      expect(api.calls('POST', `${PATH}/s3/restore`)).toHaveLength(1);
    });

    it('offers no Undo restore when the answer names no tip before', async () => {
      api.on('POST', RESTORE2, {
        step: { ...restoreStep(4, 's2'), parentStepId: null },
        audioDiscardCount: 0,
        needsAudioCount: 0,
      });
      store.view('s2');

      await store.restore();

      expect(actions.map((a) => a.label)).toEqual(['Show in Book']);
    });

    it('says when the project already was that state', async () => {
      api.on('POST', RESTORE2, { step: null, audioDiscardCount: 0, needsAudioCount: 0 });
      store.view('s2');

      await store.restore();

      expect(store.viewing()).toBeNull();
      expect(toasts).toEqual(['The project already is as it was after #2 Edit text.']);
      expect(actions).toEqual([]);
    });

    it('reports a refused Restore and stays on the step', async () => {
      api.on('POST', RESTORE2, () => problem(409, 'Project is restoring'));
      store.view('s2');

      await store.restore();

      expect(store.viewing()).toBe('s2');
      expect(errors).toEqual(['Project is restoring']);
    });

    it('tells a tab that did not restore that another one did, once the Restore finished', () => {
      const s4 = step(4, { label: "Restore to 'Split chapter' (14:02)", restoredFromStepId: 's2' });
      restoring('started');
      expect(store.restoring()).toBe(true);
      landed(s4);
      restoring('finished', s4);

      expect(store.restoring()).toBe(false);
      expect(toasts).toEqual(['Project restored to #2 Edit text in another tab']);
    });

    it('recognises its own Restore when the hub finishes before the answer lands', async () => {
      let answer!: (value: unknown) => void;
      api.on('POST', RESTORE2, () => new Promise((resolve) => (answer = resolve)));
      store.view('s2');
      const done = store.restore();
      await Promise.resolve();

      restoring('started');
      landed(restoreStep(4, 's2'));
      restoring('finished', restoreStep(4, 's2'));
      answer({ step: restoreStep(4, 's2'), audioDiscardCount: 60, needsAudioCount: 75 });
      await done;

      expect(toasts).toEqual([
        'Restored to #2 Edit text. The later steps stay in History; 75 items need generating.',
      ]);
    });

    it('sends a viewing tab back to current when a Restore starts elsewhere', async () => {
      store.view('s2');
      restoring('started');

      expect(store.viewing()).toBeNull();
      expect(store.restoring()).toBe(true);
    });

    it('names the Restore step itself when the list has never been read', async () => {
      store.close();
      store.open('dracula');
      const s4 = step(4, { label: "Restore to 'Split chapter' (14:02)", restoredFromStepId: 's2' });

      restoring('finished', s4);

      expect(toasts).toEqual([
        "Project restored to #4 Restore to 'Split chapter' (14:02) in another tab",
      ]);
    });

    it('reads the restoring flag with the list', async () => {
      store.close();
      store.open('dracula');
      api.on('GET', PATH, { ...dto([step(1)]), restoring: true });
      await store.load();

      expect(store.restoring()).toBe(true);
    });
  });
});
