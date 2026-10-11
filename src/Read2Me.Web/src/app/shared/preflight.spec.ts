import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { ApiError, PreflightApi, type PreflightPlanDto } from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices, use } from '@app/core/services';
import type { LiveMessageMap } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { initialProgress } from '@app/ui/preflight-sheet';
import type { PreflightSheet } from '@app/ui/preflight-sheet';
import { settleMicrotasks } from '../../testing/fake-timers';
import { PREFLIGHT_TASKS, Preflight, type PreflightTask } from './preflight';

/** Ported from the Angular preflight spec: the service, then the sheet (host + presentational). */
const COLD: PreflightPlanDto = {
  ready: false,
  toStart: [{ name: 'llama', status: 'Stopped' }],
  conflicts: [{ name: 'chatterbox', reason: 'GPU: one model at a time' }],
};

let api: { plan: ReturnType<typeof mock>; run: ReturnType<typeof mock> };
let toast: {
  problem: ReturnType<typeof mock>;
  warn: ReturnType<typeof mock>;
  info: ReturnType<typeof mock>;
};
const preflight$ = new Emitter<LiveMessageMap['preflight']>();
let connectionId: string | null = 'conn-1';

beforeEach(() => {
  resetServices();
  api = { plan: mock(), run: mock(async () => ({ run: 'r1' })) };
  toast = { problem: mock(), warn: mock(), info: mock() };
  connectionId = 'conn-1';
  override(PreflightApi, api as unknown as PreflightApi);
  override(ToastService, toast as unknown as ToastService);
  override(LiveService, {
    on: (_family: string, listener: (m: unknown) => void) => preflight$.subscribe(listener),
    connectionId: () => connectionId,
  } as unknown as LiveService);
});
afterEach(() => document.body.replaceChildren());

const sheetEl = () => document.querySelector<PreflightSheet>('dialog r2m-preflight-sheet');

describe('Preflight.ensureReady', () => {
  it('maps every task to a host AiTaskKind', () => {
    const kinds = Object.values(PREFLIGHT_TASKS).map((t) => t.kind);
    expect(new Set(kinds).size).toBe(kinds.length);
    expect(PREFLIGHT_TASKS.attribution.kind).toBe('CharacterAttribution');
    expect(PREFLIGHT_TASKS.audio.kind).toBe('AudioGeneration');
    expect(PREFLIGHT_TASKS.bookEdit.kind).toBe('BookEdit');
  });

  it('resolves true without a sheet when the plan is ready', async () => {
    api.plan.mockResolvedValue({ ready: true, toStart: [], conflicts: [] });
    expect(await use(Preflight).ensureReady('transcription')).toBe(true);
    expect(api.plan).toHaveBeenCalledWith('Transcription');
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('opens the sheet as a bottom sheet with the plan and answers what the sheet answered', async () => {
    api.plan.mockResolvedValue(COLD);
    const pending = use(Preflight).ensureReady('attribution');
    await settleMicrotasks();
    const dialog = document.querySelector('dialog')!;
    expect(dialog.className).toBe('r2m-dialog r2m-dialog--sheet');
    expect(dialog.getAttribute('closedby')).toBe('none');
    const sheet = sheetEl()!;
    expect(sheet.data).toEqual({ kind: 'CharacterAttribution', label: 'Attribution', plan: COLD });
    sheet.dispatchEvent(new CustomEvent('r2m-close', { detail: true }));
    expect(await pending).toBe(true);

    const second = use(Preflight).ensureReady('audio');
    await settleMicrotasks();
    document.querySelector('dialog')!.close();
    expect(await second).toBe(false);
  });

  it('toasts a failed plan and resolves false', async () => {
    api.plan.mockRejectedValue(new ApiError(500, 'Boom', 'llama config missing'));
    expect(await use(Preflight).ensureReady('discovery')).toBe(false);
    expect(toast.problem).toHaveBeenCalled();
    expect(document.querySelector('dialog')).toBeNull();
  });
});

describe('r2m-preflight-sheet', () => {
  async function mount(plan: PreflightPlanDto = COLD) {
    const closed: unknown[] = [];
    const sheet = document.createElement('r2m-preflight-sheet');
    sheet.data = { kind: 'CharacterAttribution', label: 'Attribution', plan };
    sheet.addEventListener('r2m-close', (e) => closed.push((e as CustomEvent).detail));
    document.body.append(sheet);
    await sheet.rendered();
    const buttons = () => Array.from(sheet.querySelectorAll<HTMLButtonElement>('footer button'));
    const texts = (selector: string) =>
      Array.from(sheet.querySelectorAll(selector)).map((n) => n.textContent?.trim() ?? '');
    const start = async () => {
      buttons()[1]!.click();
      await settleMicrotasks();
      await sheet.rendered();
    };
    return { sheet, closed, buttons, texts, start };
  }

  it('orders the rows conflicts first', () => {
    expect(initialProgress(COLD)).toEqual([
      { name: 'chatterbox', stage: 'waitingToStop' },
      { name: 'llama', stage: 'waitingToStart' },
    ]);
  });

  it('plan phase lists conflicts and services to start with Cancel / Start', async () => {
    const { sheet, buttons, texts } = await mount();
    expect(sheet.querySelector('h2')?.textContent).toBe('Attribution needs AI services');
    expect(texts('.r2m-preflight-sheet__name')).toEqual(['chatterbox', 'llama']);
    expect(sheet.querySelector('.r2m-preflight-sheet__reason')?.textContent).toBe(
      'GPU: one model at a time',
    );
    expect(sheet.querySelector('.r2m-status-chip')?.textContent).toContain('Stopped');
    expect(buttons().map((b) => b.textContent?.trim())).toEqual(['Cancel', 'Start services']);
  });

  it('cancel in the plan phase closes false without running anything', async () => {
    const { closed, buttons } = await mount();
    buttons()[0]!.click();
    expect(closed).toEqual([false]);
    expect(api.run).not.toHaveBeenCalled();
  });

  it('Start runs the task kind on this connection and renders the stages until done ok', async () => {
    const { sheet, closed, start } = await mount();
    await start();
    expect(api.run).toHaveBeenCalledWith('CharacterAttribution', { connectionId: 'conn-1' });
    expect(sheet.querySelector('h2')?.textContent).toBe('Starting services');
    expect(sheet.classList.contains('r2m-preflight-sheet--running')).toBe(true);

    preflight$.emit({ kind: 'stage', run: 'r1', name: 'chatterbox', stage: 'stopping' });
    preflight$.emit({ kind: 'stage', run: 'other', name: 'llama', stage: 'failed', error: 'x' });
    await sheet.rendered();
    const rows = Array.from(sheet.querySelectorAll('.r2m-preflight-sheet__row'));
    expect(rows[0]?.getAttribute('data-stage')).toBe('stopping');
    expect(rows[0]?.querySelector('.r2m-spinner')).not.toBeNull();
    expect(rows[1]?.getAttribute('data-stage')).toBe('waitingToStart');
    expect(rows[1]?.querySelector('.r2m-icon')?.textContent).toBe('schedule');

    preflight$.emit({ kind: 'stage', run: 'r1', name: 'chatterbox', stage: 'stopped' });
    preflight$.emit({ kind: 'stage', run: 'r1', name: 'llama', stage: 'starting' });
    await sheet.rendered();
    expect(rows[0]?.querySelector('.r2m-icon')?.textContent).toBe('stop_circle');
    expect(rows[1]?.querySelector('.r2m-status-chip')?.textContent).toContain('Starting');

    preflight$.emit({ kind: 'stage', run: 'r1', name: 'llama', stage: 'ready' });
    preflight$.emit({ kind: 'done', run: 'r1', ok: true });
    expect(closed).toEqual([true]);
    await sheet.rendered();
    expect(sheet.textContent).toContain('All required services are ready');
  });

  it('a stage that lands before the 202 answers is not lost', async () => {
    let answer: (v: { run: string }) => void = () => undefined;
    api.run.mockReturnValue(new Promise<{ run: string }>((resolve) => (answer = resolve)));
    const { sheet, buttons } = await mount();
    buttons()[1]!.click();
    preflight$.emit({ kind: 'stage', run: 'r2', name: 'chatterbox', stage: 'stopping' });
    answer({ run: 'r2' });
    await settleMicrotasks();
    await sheet.rendered();
    expect(sheet.querySelector('.r2m-preflight-sheet__row')?.getAttribute('data-stage')).toBe(
      'stopping',
    );
  });

  it('done not ok shows the failure summary and Close closes false', async () => {
    const { sheet, closed, buttons, texts, start } = await mount();
    await start();
    preflight$.emit({
      kind: 'stage',
      run: 'r1',
      name: 'llama',
      stage: 'failed',
      error: 'health check timed out',
    });
    preflight$.emit({
      kind: 'done',
      run: 'r1',
      ok: false,
      reason: 'Failed to start llama: health check timed out',
    });
    await sheet.rendered();
    expect(sheet.querySelector('.r2m-preflight-sheet__error')?.textContent).toContain(
      'Failed to start llama',
    );
    expect(texts('.r2m-preflight-sheet__reason')).toEqual(['health check timed out']);
    buttons()[0]!.click();
    expect(closed).toEqual([false]);
  });

  it('cancel while running closes false and says the services keep starting', async () => {
    const { closed, buttons, start } = await mount();
    await start();
    buttons()[0]!.click();
    expect(toast.info).toHaveBeenCalled();
    expect(closed).toEqual([false]);
  });

  it('refuses to start without a hub connection', async () => {
    connectionId = null;
    const { sheet, start } = await mount();
    await start();
    expect(api.run).not.toHaveBeenCalled();
    expect(toast.warn).toHaveBeenCalled();
    expect(sheet.querySelector('h2')?.textContent).toContain('needs AI services');
  });

  it('stops listening to the hub once removed', async () => {
    const { sheet, start } = await mount();
    await start();
    sheet.remove();
    preflight$.emit({ kind: 'done', run: 'r1', ok: true });
    expect(sheet.phase()).toBe('running');
  });
});

/**
 * Angular's tickets 09–19 stubbed readiness as "always ready" behind `Preflight.ensureReady`. This
 * scan fails if the stub — or a task kind the host does not know — creeps into the native app.
 */
describe('preflight stub replacement', () => {
  const APP_DIR = resolve(import.meta.dir, '..');

  function sources(dir: string, out: string[] = []): string[] {
    for (const entry of readdirSync(dir)) {
      const path = join(dir, entry);
      if (statSync(path).isDirectory()) sources(path, out);
      else if (path.endsWith('.ts') && !path.endsWith('.spec.ts')) out.push(path);
    }
    return out;
  }

  it('no source outside the preflight seam resolves readiness on its own', () => {
    const offenders = sources(APP_DIR).filter((path) => {
      if (path.endsWith('preflight.ts')) return false;
      const text = readFileSync(path, 'utf8');
      return (
        /ensureReady\s*\([^)]*\)\s*(?::[^{]*)?\{/.test(text) || text.includes('reported ready')
      );
    });
    expect(offenders).toEqual([]);
  });

  it('every ensureReady call names a task the host knows', () => {
    const known = new Set(Object.keys(PREFLIGHT_TASKS) as PreflightTask[]);
    const unknown: string[] = [];
    for (const path of sources(APP_DIR)) {
      for (const m of readFileSync(path, 'utf8').matchAll(/ensureReady\('([a-zA-Z]+)'\)/g)) {
        if (!known.has(m[1] as PreflightTask)) unknown.push(`${path}: ${m[1]}`);
      }
    }
    expect(unknown).toEqual([]);
  });
});
