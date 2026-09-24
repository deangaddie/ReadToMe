import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { TestBed } from '@angular/core/testing';
import {
  MAT_BOTTOM_SHEET_DATA,
  MatBottomSheet,
  MatBottomSheetRef,
} from '@angular/material/bottom-sheet';
import { Subject, of } from 'rxjs';
import { AiServiceStatus, ApiError, PreflightApi, PreflightPlanDto } from '@app/api';
import { LiveMessageMap } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { PREFLIGHT_TASKS, Preflight, PreflightTask } from './preflight';
import { PreflightSheetHost, initialProgress } from './preflight-sheet-host';

const COLD: PreflightPlanDto = {
  ready: false,
  toStart: [{ name: 'llama', status: 'Stopped' as AiServiceStatus }],
  conflicts: [{ name: 'chatterbox', reason: 'GPU: one model at a time' }],
};

describe('Preflight.ensureReady', () => {
  let api: { plan: ReturnType<typeof vi.fn>; run: ReturnType<typeof vi.fn> };
  let sheet: { open: ReturnType<typeof vi.fn> };
  let toast: {
    problem: ReturnType<typeof vi.fn>;
    warn: ReturnType<typeof vi.fn>;
    info: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    api = { plan: vi.fn(), run: vi.fn() };
    sheet = { open: vi.fn() };
    toast = { problem: vi.fn(), warn: vi.fn(), info: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: PreflightApi, useValue: api },
        { provide: MatBottomSheet, useValue: sheet },
        { provide: ToastService, useValue: toast },
      ],
    });
  });

  it('maps every task to a host AiTaskKind', () => {
    const kinds = Object.values(PREFLIGHT_TASKS).map((t) => t.kind);
    expect(new Set(kinds).size).toBe(kinds.length);
    expect(PREFLIGHT_TASKS.attribution.kind).toBe('CharacterAttribution');
    expect(PREFLIGHT_TASKS.audio.kind).toBe('AudioGeneration');
    expect(PREFLIGHT_TASKS.bookEdit.kind).toBe('BookEdit');
  });

  it('resolves true without a sheet when the plan is ready', async () => {
    api.plan.mockResolvedValue({ ready: true, toStart: [], conflicts: [] });
    expect(await TestBed.inject(Preflight).ensureReady('transcription')).toBe(true);
    expect(api.plan).toHaveBeenCalledWith('Transcription');
    expect(sheet.open).not.toHaveBeenCalled();
  });

  it('opens the sheet with the plan otherwise and answers what the sheet answered', async () => {
    api.plan.mockResolvedValue(COLD);
    sheet.open.mockReturnValue({ afterDismissed: () => of(true) });
    expect(await TestBed.inject(Preflight).ensureReady('attribution')).toBe(true);
    const [component, config] = sheet.open.mock.calls[0]!;
    expect(component).toBe(PreflightSheetHost);
    expect(config.data).toEqual({ kind: 'CharacterAttribution', label: 'Attribution', plan: COLD });
    expect(config.disableClose).toBe(true);

    sheet.open.mockReturnValue({ afterDismissed: () => of(undefined) });
    expect(await TestBed.inject(Preflight).ensureReady('audio')).toBe(false);
  });

  it('toasts a failed plan and resolves false', async () => {
    api.plan.mockRejectedValue(new ApiError(500, 'Boom', 'llama config missing'));
    expect(await TestBed.inject(Preflight).ensureReady('discovery')).toBe(false);
    expect(toast.problem).toHaveBeenCalled();
    expect(sheet.open).not.toHaveBeenCalled();
  });
});

describe('PreflightSheetHost', () => {
  let api: { run: ReturnType<typeof vi.fn> };
  let ref: { dismiss: ReturnType<typeof vi.fn> };
  let toast: { warn: ReturnType<typeof vi.fn>; info: ReturnType<typeof vi.fn> };
  const preflight$ = new Subject<LiveMessageMap['preflight']>();
  let connectionId: string | null = 'conn-1';

  function mount(plan: PreflightPlanDto = COLD) {
    TestBed.configureTestingModule({
      providers: [
        { provide: PreflightApi, useValue: api },
        { provide: MatBottomSheetRef, useValue: ref },
        { provide: ToastService, useValue: toast },
        {
          provide: LiveService,
          useValue: { on: () => preflight$.asObservable(), connectionId: () => connectionId },
        },
        {
          provide: MAT_BOTTOM_SHEET_DATA,
          useValue: { kind: 'CharacterAttribution', label: 'Attribution', plan },
        },
      ],
    });
    const fixture = TestBed.createComponent(PreflightSheetHost);
    fixture.detectChanges();
    return fixture;
  }

  function texts(el: HTMLElement, selector: string): string[] {
    return Array.from(el.querySelectorAll(selector)).map((n) => n.textContent?.trim() ?? '');
  }

  beforeEach(() => {
    api = { run: vi.fn().mockResolvedValue({ run: 'r1' }) };
    ref = { dismiss: vi.fn() };
    toast = { warn: vi.fn(), info: vi.fn() };
    connectionId = 'conn-1';
  });

  it('orders the rows conflicts first', () => {
    expect(initialProgress(COLD)).toEqual([
      { name: 'chatterbox', stage: 'waitingToStop' },
      { name: 'llama', stage: 'waitingToStart' },
    ]);
  });

  it('cancel in the plan phase dismisses false without running anything', () => {
    const fixture = mount();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('footer button') as HTMLButtonElement).click();
    expect(ref.dismiss).toHaveBeenCalledWith(false);
    expect(api.run).not.toHaveBeenCalled();
  });

  it('Start runs the task kind on this connection and renders the stages until done ok', async () => {
    const fixture = mount();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelectorAll('footer button')[1] as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(api.run).toHaveBeenCalledWith('CharacterAttribution', { connectionId: 'conn-1' });
    expect(el.querySelector('h2')?.textContent).toBe('Starting services');

    preflight$.next({ kind: 'stage', run: 'r1', name: 'chatterbox', stage: 'stopping' });
    preflight$.next({ kind: 'stage', run: 'other', name: 'llama', stage: 'failed', error: 'x' });
    await fixture.whenStable();
    const rows = Array.from(el.querySelectorAll('.r2m-preflight-sheet__row'));
    expect(rows[0]?.getAttribute('data-stage')).toBe('stopping');
    expect(rows[1]?.getAttribute('data-stage')).toBe('waitingToStart');

    preflight$.next({ kind: 'stage', run: 'r1', name: 'chatterbox', stage: 'stopped' });
    preflight$.next({ kind: 'stage', run: 'r1', name: 'llama', stage: 'starting' });
    preflight$.next({ kind: 'stage', run: 'r1', name: 'llama', stage: 'ready' });
    preflight$.next({ kind: 'done', run: 'r1', ok: true });
    expect(ref.dismiss).toHaveBeenCalledWith(true);
  });

  it('a stage that lands before the 202 answers is not lost', async () => {
    let answer: (v: { run: string }) => void = () => undefined;
    api.run.mockReturnValue(new Promise<{ run: string }>((resolve) => (answer = resolve)));
    const fixture = mount();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelectorAll('footer button')[1] as HTMLButtonElement).click();
    preflight$.next({ kind: 'stage', run: 'r2', name: 'chatterbox', stage: 'stopping' });
    answer({ run: 'r2' });
    await fixture.whenStable();
    expect(el.querySelector('.r2m-preflight-sheet__row')?.getAttribute('data-stage')).toBe(
      'stopping',
    );
  });

  it('done not ok shows the failure summary and Close dismisses false', async () => {
    const fixture = mount();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelectorAll('footer button')[1] as HTMLButtonElement).click();
    await fixture.whenStable();
    preflight$.next({
      kind: 'stage',
      run: 'r1',
      name: 'llama',
      stage: 'failed',
      error: 'health check timed out',
    });
    preflight$.next({
      kind: 'done',
      run: 'r1',
      ok: false,
      reason: 'Failed to start llama: health check timed out',
    });
    await fixture.whenStable();
    expect(el.querySelector('.r2m-preflight-sheet__error')?.textContent).toContain(
      'Failed to start llama',
    );
    expect(texts(el, '.r2m-preflight-sheet__reason')).toEqual(['health check timed out']);
    (el.querySelector('footer button') as HTMLButtonElement).click();
    expect(ref.dismiss).toHaveBeenCalledWith(false);
  });

  it('cancel while running dismisses false and says the services keep starting', async () => {
    const fixture = mount();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelectorAll('footer button')[1] as HTMLButtonElement).click();
    await fixture.whenStable();
    (el.querySelector('footer button') as HTMLButtonElement).click();
    expect(toast.info).toHaveBeenCalled();
    expect(ref.dismiss).toHaveBeenCalledWith(false);
  });

  it('refuses to start without a hub connection', async () => {
    connectionId = null;
    const fixture = mount();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelectorAll('footer button')[1] as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(api.run).not.toHaveBeenCalled();
    expect(toast.warn).toHaveBeenCalled();
    expect(el.querySelector('h2')?.textContent).toContain('needs AI services');
  });
});

/**
 * Tickets 09–19 stubbed readiness as "always ready" behind `Preflight.ensureReady`; ticket 25
 * replaces it. This scan fails if the stub — or a task kind the host does not know — creeps back.
 */
describe('preflight stub replacement', () => {
  const APP_DIR = resolve(process.cwd(), 'src/app');

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
