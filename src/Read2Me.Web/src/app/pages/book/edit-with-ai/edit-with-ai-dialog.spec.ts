import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { ApiError, BookEditRow, BookEditRunDto, PlanBookEditResponse } from '@app/api';
import { LiveService } from '@app/live';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { Subject } from 'rxjs';
import { BookEditor } from '../book-editor';
import { EditWithAiDialog } from './edit-with-ai-dialog';
import type { BookEditMessage } from '@app/live';

const PLAN: PlanBookEditResponse = {
  status: 'Ok',
  reason: null,
  summary: 'Edit chapter titles (whole book) — rewritten by the AI',
  program: 'prog-1',
  transform: 'Llm',
  targetCount: 2,
  requestCount: 1,
  warnings: [],
};

function row(id: string, oldValue: string, newValue: string | null): BookEditRow {
  return {
    kind: 'ChapterTitle',
    id,
    displayPath: `Book / ${oldValue}`,
    oldValue,
    newValue,
    status: newValue === null ? 'Failed' : 'Proposed',
    failureReason: newValue === null ? 'model returned nothing' : null,
  };
}

const ROWS = [row('c1', 'chapter one', 'Chapter One'), row('c2', 'chapter two', 'Chapter Two')];

const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

describe('EditWithAiDialog', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<EditWithAiDialog>;
  let messages: Subject<BookEditMessage>;
  let closed: unknown;
  let connectionId: string | null;
  let confirmed: boolean;
  let applyError: ApiError | null;
  let applied: unknown[];

  const el = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const button = (action: string) =>
    el().querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
  const text = (testId: string) =>
    el().querySelector<HTMLElement>(`[data-testid="${testId}"]`)?.textContent?.trim() ?? null;
  const phase = () => el().querySelector('.edit')?.getAttribute('data-phase');
  const rows = () => Array.from(el().querySelectorAll<HTMLElement>('[data-row]'));

  async function render(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    messages = new Subject<BookEditMessage>();
    closed = undefined;
    connectionId = 'conn-1';
    confirmed = true;
    applyError = null;
    applied = [];

    await TestBed.configureTestingModule({
      imports: [EditWithAiDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: MAT_DIALOG_DATA, useValue: { folder: 'dune' } },
        {
          provide: MatDialogRef,
          useValue: { close: (result?: unknown) => (closed = result ?? null) },
        },
        {
          provide: LiveService,
          useValue: { connectionId: () => connectionId, on: () => messages.asObservable() },
        },
        {
          provide: LlmStreamFeed,
          // The stream group is the activity centre's business; here it only has to be joinable.
          useValue: { events: signal([]), acquire: () => undefined, release: () => undefined },
        },
        {
          provide: BookEditor,
          useValue: {
            tryExecute: (command: unknown) => {
              applied.push(command);
              return Promise.resolve(applyError);
            },
          },
        },
        { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(confirmed) } },
        { provide: Preflight, useValue: { ensureReady: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(EditWithAiDialog);
  });

  afterEach(() => {
    // Closing the dialog tells the host it is done with the plan; every test ends that way.
    fixture.destroy();
    expectSessionDropped();
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  async function instruct(instruction = 'capitalise chapter titles'): Promise<void> {
    await render();
    const field = el().querySelector<HTMLTextAreaElement>('[data-testid="instruction"]')!;
    field.value = instruction;
    field.dispatchEvent(new Event('input'));
    await render();
  }

  async function analyze(plan: PlanBookEditResponse = PLAN): Promise<void> {
    button('analyze').click();
    await settle();
    http.expectOne('/api/projects/dune/book-edits/plan').flush(plan);
    await settle();
    await render();
  }

  async function generate(): Promise<void> {
    button('generate').click();
    await settle();
    http.expectOne('/api/projects/dune/book-edits/prog-1/propose').flush(
      { started: true },
      { status: 202, statusText: 'Accepted' },
    );
    await settle();
    await render();
  }

  async function push(message: BookEditMessage): Promise<void> {
    messages.next(message);
    await settle();
    await render();
  }

  /** Drives instruct → plan → proposing → review with the two rows landed. */
  async function reachReview(): Promise<void> {
    await instruct();
    await analyze();
    await generate();
    await push({ kind: 'done', program: 'prog-1', done: 2, total: 2, cancelled: false, rows: ROWS });
  }

  /** The dialog drops its session on teardown once a plan exists. */
  function expectSessionDropped(): void {
    const drops = http.match(
      (r) => r.method === 'DELETE' && r.url === '/api/projects/dune/book-edits/prog-1',
    );
    drops.forEach((d) => d.flush(null, { status: 204, statusText: 'No Content' }));
  }

  it('starts on Instruct with Analyze disabled until an instruction is typed', async () => {
    await render();

    expect(phase()).toBe('instruct');
    expect(button('analyze').disabled).toBe(true);

    await instruct();
    expect(button('analyze').disabled).toBe(false);
  });

  it('keeps a refused plan on Instruct and explains the status', async () => {
    await instruct();

    await analyze({ ...PLAN, status: 'NoLlmConfigured', reason: 'none', program: null });

    expect(phase()).toBe('instruct');
    expect(text('edit-error')).toContain('No LLM server is configured');
  });

  it('explains a plan that matched nothing', async () => {
    await instruct();

    await analyze({ ...PLAN, status: 'NoTargets', reason: null, program: null, targetCount: 0 });

    expect(phase()).toBe('instruct');
    expect(text('edit-error')).toContain('No items in the book match');
  });

  it('shows what the host understood, and its warnings, on Plan', async () => {
    await instruct();

    await analyze({ ...PLAN, targetCount: 400, requestCount: 50, warnings: ['This is a large AI job (400 items).'] });

    expect(phase()).toBe('plan');
    expect(text('plan-summary')).toContain('Edit chapter titles');
    expect(text('plan-counts')).toContain('400 matching items');
    expect(text('plan-counts')).toContain('50 requests');
    expect(text('plan-warning')).toContain('large AI job');
  });

  it('sends the instruction and the thinking flags it was given', async () => {
    await instruct('rename the chapters');
    el().querySelector<HTMLElement>('[data-testid="plan-thinking"] button')!.click();
    await render();

    button('analyze').click();
    await settle();
    const plan = http.expectOne('/api/projects/dune/book-edits/plan');
    expect(plan.request.body).toEqual({ instruction: 'rename the chapters', thinking: true });
    plan.flush(PLAN);
    await settle();
    await render();

    el().querySelector<HTMLElement>('[data-testid="fix-thinking"] button')!.click();
    await render();
    button('generate').click();
    await settle();
    const propose = http.expectOne('/api/projects/dune/book-edits/prog-1/propose');
    expect(propose.request.body).toEqual({ thinking: true, connectionId: 'conn-1' });
    propose.flush({ started: true }, { status: 202, statusText: 'Accepted' });
    await settle();

  });

  it('follows the run on the hub and lands every appliable row ticked', async () => {
    await instruct();
    await analyze();
    await generate();
    expect(phase()).toBe('proposing');

    await push({ kind: 'progress', program: 'prog-1', done: 1, total: 2 });
    expect(text('progress')).toContain('1 of 2');

    await push({ kind: 'done', program: 'prog-1', done: 2, total: 2, cancelled: false, rows: ROWS });

    expect(phase()).toBe('review');
    expect(rows()).toHaveLength(2);
    expect(text('selected-count')).toBe('2 of 2 selected');

  });

  it('ignores a run that belongs to another plan', async () => {
    await instruct();
    await analyze();
    await generate();

    await push({ kind: 'done', program: 'other', done: 1, total: 1, cancelled: false, rows: ROWS });

    expect(phase()).toBe('proposing');
  });

  it('keeps the rows a cancelled run computed', async () => {
    await instruct();
    await analyze();
    await generate();

    button('cancel-proposing').click();
    await settle();
    http.expectOne('/api/projects/dune/book-edits/prog-1/cancel').flush(null);
    await push({
      kind: 'done',
      program: 'prog-1',
      done: 1,
      total: 2,
      cancelled: true,
      rows: [ROWS[0]!],
    });

    expect(phase()).toBe('review');
    expect(rows()).toHaveLength(1);
    expect(text('selected-count')).toBe('1 of 1 selected');

  });

  it('reports a failed run and goes back to the plan', async () => {
    await instruct();
    await analyze();
    await generate();

    await push({ kind: 'failed', program: 'prog-1', reason: 'the model went away' });

    expect(phase()).toBe('plan');
    expect(text('edit-error')).toContain('the model went away');
  });

  it('polls the run when there is no hub connection to push to', async () => {
    connectionId = null;
    await instruct();
    await analyze();

    button('generate').click();
    await settle();
    const propose = http.expectOne('/api/projects/dune/book-edits/prog-1/propose');
    expect(propose.request.body).toEqual({ thinking: false, connectionId: null });
    propose.flush({ started: true }, { status: 202, statusText: 'Accepted' });
    await settle(500);

    const running: BookEditRunDto = { status: 'Running', done: 1, total: 2, rows: [], reason: null };
    http.expectOne('/api/projects/dune/book-edits/prog-1').flush(running);
    await settle(500);
    await render();
    expect(text('progress')).toContain('1 of 2');

    const done: BookEditRunDto = { status: 'Completed', done: 2, total: 2, rows: ROWS, reason: null };
    http.expectOne('/api/projects/dune/book-edits/prog-1').flush(done);
    await settle();
    await render();

    expect(phase()).toBe('review');
    expect(rows()).toHaveLength(2);
  });

  it('applies the ticked rows as one book command, with hand edits included', async () => {
    await reachReview();

    // Untick the second row, then hand-edit the first.
    rows()[1]!.querySelector<HTMLElement>('input[type=checkbox]')!.click();
    await render();
    rows()[0]!.querySelector<HTMLButtonElement>('[data-action="open-row"]')!.click();
    await render();
    const proposed = el().querySelector<HTMLTextAreaElement>('[data-testid="proposed"]')!;
    proposed.value = 'Chapter I';
    proposed.dispatchEvent(new Event('input'));
    await render();

    expect(text('selected-count')).toBe('1 of 2 selected');
    expect(button('apply').textContent).toContain('Apply 1 selected');

    button('apply').click();
    await settle();

    expect(applied).toEqual([
      { type: 'ApplyBookEdits', edits: [{ kind: 'ChapterTitle', id: 'c1', newValue: 'Chapter I' }] },
    ]);
    expect(closed).toEqual({ applied: 1 });
  });

  it('keeps the dialog open with the reviewed rows when applying is refused', async () => {
    await reachReview();
    applyError = new ApiError(422, 'Unprocessable', 'the book moved on');

    button('apply').click();
    await settle();
    await render();

    expect(closed).toBeUndefined();
    expect(phase()).toBe('review');
    expect(rows()).toHaveLength(2);
    expect(text('edit-error')).toContain('the book moved on');
  });

  it('re-asks the AI for one row with the sticky hint and takes the answer onto it', async () => {
    await reachReview();
    rows()[0]!.querySelector<HTMLButtonElement>('[data-action="open-row"]')!.click();
    await render();
    const hint = el().querySelector<HTMLInputElement>('[data-testid="hint"]')!;
    hint.value = 'spell the number out';
    hint.dispatchEvent(new Event('input'));
    await render();

    button('retry-row').click();
    await settle();
    const retry = http.expectOne('/api/projects/dune/book-edits/prog-1/propose-one');
    expect(retry.request.body).toEqual({
      targetId: 'c1',
      hint: 'spell the number out',
      thinking: false,
    });
    retry.flush(row('c1', 'chapter one', 'Chapter 1'));
    await settle();
    await render();

    expect(rows()[0]!.textContent).toContain('Chapter 1');
    expect(text('selected-count')).toBe('2 of 2 selected');
  });

  it('keeps the row as it was when a retry never gets an answer', async () => {
    await reachReview();
    rows()[0]!.querySelector<HTMLButtonElement>('[data-action="open-row"]')!.click();
    await render();
    const proposed = el().querySelector<HTMLTextAreaElement>('[data-testid="proposed"]')!;
    proposed.value = 'Chapter I';
    proposed.dispatchEvent(new Event('input'));
    await render();

    button('retry-row').click();
    await settle();
    http
      .expectOne('/api/projects/dune/book-edits/prog-1/propose-one')
      .flush({ title: 'Bad Gateway' }, { status: 502, statusText: 'Bad Gateway' });
    await settle();
    await render();

    // The hand edit and the AI's own proposal both survive; the failure sits on the row.
    expect(text('retry-error')).toContain('Asking the AI again failed');
    expect(el().querySelector<HTMLTextAreaElement>('[data-testid="proposed"]')!.value).toBe(
      'Chapter I',
    );
    expect(text('selected-count')).toBe('2 of 2 selected');
  });

  it('confirms before throwing hand edits away, and drops the session on Start over', async () => {
    await reachReview();
    rows()[0]!.querySelector<HTMLButtonElement>('[data-action="open-row"]')!.click();
    await render();
    const proposed = el().querySelector<HTMLTextAreaElement>('[data-testid="proposed"]')!;
    proposed.value = 'Chapter I';
    proposed.dispatchEvent(new Event('input'));
    await render();

    confirmed = false;
    button('start-over').click();
    await settle();
    await render();
    expect(phase()).toBe('review');

    confirmed = true;
    button('start-over').click();
    await settle();
    http
      .expectOne(
        (r) => r.method === 'DELETE' && r.url === '/api/projects/dune/book-edits/prog-1',
      )
      .flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    await render();

    expect(phase()).toBe('instruct');
  });
});
