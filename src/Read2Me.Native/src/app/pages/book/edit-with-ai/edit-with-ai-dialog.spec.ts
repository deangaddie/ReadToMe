import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import {
  ApiError,
  type BookEditRow,
  type BookEditRunDto,
  type PlanBookEditResponse,
} from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { BookEditMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/dialogs';
import { FakeApi, problem } from '../../../../testing/fake-api';
import type { BookEditor } from '../book-editor';
import { type EditWithAiDialog, openEditWithAiDialog } from './edit-with-ai-dialog';
import './edit-with-ai-dialog';

/** Ported from the Angular TestBed spec, case for case. */
const BASE = '/api/projects/dune/book-edits';

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

/** Every request the dialog may make; anything else is a bug the teardown catches. */
const ROUTED = new Set([
  `POST ${BASE}/plan`,
  `POST ${BASE}/prog-1/propose`,
  `GET ${BASE}/prog-1`,
  `POST ${BASE}/prog-1/propose-one`,
  `POST ${BASE}/prog-1/cancel`,
  `DELETE ${BASE}/prog-1`,
]);

describe('r2m-edit-with-ai-dialog', () => {
  let api: FakeApi;
  let dialog: EditWithAiDialog;
  let result: Promise<{ applied: number } | null>;
  let messages: Emitter<BookEditMessage>;
  let connectionId: string | null;
  let confirmed: boolean;
  let applyError: ApiError | null;
  let applied: unknown[];
  let plan: PlanBookEditResponse;

  const button = (action: string) =>
    dialog.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
  const text = (testId: string) =>
    dialog
      .querySelector<HTMLElement>(`[data-testid="${testId}"]`)
      ?.textContent?.replace(/\s+/g, ' ')
      .trim() ?? null;
  const phase = () => dialog.querySelector('.edit')?.getAttribute('data-phase');
  const rows = () => Array.from(dialog.querySelectorAll<HTMLElement>('[data-row]'));
  const type = (field: HTMLInputElement | HTMLTextAreaElement, value: string) => {
    field.value = value;
    field.dispatchEvent(new Event('input'));
  };
  const toggle = (testId: string) => {
    const box = dialog.querySelector<HTMLInputElement>(`[data-testid="${testId}"] input`)!;
    box.checked = !box.checked;
    box.dispatchEvent(new Event('change'));
  };

  beforeEach(async () => {
    resetServices();
    api = new FakeApi();
    api.install();
    messages = new Emitter<BookEditMessage>();
    connectionId = 'conn-1';
    confirmed = true;
    applyError = null;
    applied = [];
    plan = PLAN;
    override(LiveService, {
      connectionId: () => connectionId,
      on: (_family: string, listener: (m: BookEditMessage) => void) => messages.subscribe(listener),
    } as unknown as LiveService);
    // The stream group is the activity centre's business; here it only has to be joinable.
    override(LlmStreamFeed, {
      events: signal([]),
      acquire: () => undefined,
      release: () => undefined,
    } as unknown as LlmStreamFeed);
    override(ConfirmService, { confirm: async () => confirmed } as unknown as ConfirmService);
    override(Preflight, { ensureReady: async () => true } as unknown as Preflight);
    api
      .on('POST', `${BASE}/plan`, () => plan)
      .on('POST', `${BASE}/prog-1/propose`, () => Response.json({ started: true }, { status: 202 }))
      .on('POST', `${BASE}/prog-1/cancel`, undefined)
      .on('DELETE', `${BASE}/prog-1`, undefined);

    const editor = {
      tryExecute: (command: unknown) => {
        applied.push(command);
        return Promise.resolve(applyError);
      },
    } as unknown as BookEditor;
    result = openEditWithAiDialog('dune', editor);
    await Promise.resolve();
    dialog = document.querySelector<EditWithAiDialog>('r2m-edit-with-ai-dialog')!;
    await dialog.rendered();
  });

  afterEach(async () => {
    // Closing the dialog tells the host it is done with the plan; every test ends that way.
    document.body.replaceChildren();
    await settle();
    const granted = api.calls('POST', `${BASE}/plan`).length > 0 && plan.program !== null;
    expect(api.calls('DELETE', `${BASE}/prog-1`).length > 0).toBe(granted);
    // Nothing the dialog sent went unanswered (HttpTestingController's verify).
    const unrouted = api.requests.filter((r) => !ROUTED.has(`${r.method} ${r.path}`));
    expect(unrouted).toEqual([]);
  });

  async function instruct(instruction = 'capitalise chapter titles'): Promise<void> {
    type(dialog.querySelector<HTMLTextAreaElement>('[data-testid="instruction"]')!, instruction);
    await dialog.rendered();
  }

  async function analyze(): Promise<void> {
    button('analyze').click();
    await settle();
    await dialog.rendered();
  }

  async function generate(): Promise<void> {
    button('generate').click();
    await settle();
    await dialog.rendered();
  }

  async function push(message: BookEditMessage): Promise<void> {
    messages.emit(message);
    await settle();
    await dialog.rendered();
  }

  /** Drives instruct → plan → proposing → review with the two rows landed. */
  async function reachReview(): Promise<void> {
    await instruct();
    await analyze();
    await generate();
    await push({
      kind: 'done',
      program: 'prog-1',
      done: 2,
      total: 2,
      cancelled: false,
      rows: ROWS,
    });
  }

  async function openRow(index: number): Promise<void> {
    rows()[index]!.querySelector<HTMLButtonElement>('[data-action="open-row"]')!.click();
    await dialog.rendered();
  }

  const proposed = () => dialog.querySelector<HTMLTextAreaElement>('[data-testid="proposed"]')!;

  it('starts on Instruct with Analyze disabled until an instruction is typed', async () => {
    expect(phase()).toBe('instruct');
    expect(button('analyze').disabled).toBe(true);

    await instruct();
    expect(button('analyze').disabled).toBe(false);
  });

  it('keeps a refused plan on Instruct and explains the status', async () => {
    plan = { ...PLAN, status: 'NoLlmConfigured', reason: 'none', program: null };
    await instruct();

    await analyze();

    expect(phase()).toBe('instruct');
    expect(text('edit-error')).toContain('No LLM server is configured');
  });

  it('explains a plan that matched nothing', async () => {
    plan = { ...PLAN, status: 'NoTargets', reason: null, program: null, targetCount: 0 };
    await instruct();

    await analyze();

    expect(phase()).toBe('instruct');
    expect(text('edit-error')).toContain('No items in the book match');
  });

  it('shows what the host understood, and its warnings, on Plan', async () => {
    plan = {
      ...PLAN,
      targetCount: 400,
      requestCount: 50,
      warnings: ['This is a large AI job (400 items).'],
    };
    await instruct();

    await analyze();

    expect(phase()).toBe('plan');
    expect(text('plan-summary')).toContain('Edit chapter titles');
    expect(text('plan-counts')).toContain('400 matching items');
    expect(text('plan-counts')).toContain('50 requests');
    expect(text('plan-warning')).toContain('large AI job');
  });

  it('sends the instruction and the thinking flags it was given', async () => {
    await instruct('rename the chapters');
    toggle('plan-thinking');
    await dialog.rendered();

    await analyze();
    expect(api.calls('POST', `${BASE}/plan`)[0]?.body).toEqual({
      instruction: 'rename the chapters',
      thinking: true,
    });

    toggle('fix-thinking');
    await dialog.rendered();
    await generate();
    expect(api.calls('POST', `${BASE}/prog-1/propose`)[0]?.body).toEqual({
      thinking: true,
      connectionId: 'conn-1',
    });
  });

  it('follows the run on the hub and lands every appliable row ticked', async () => {
    await instruct();
    await analyze();
    await generate();
    expect(phase()).toBe('proposing');

    await push({ kind: 'progress', program: 'prog-1', done: 1, total: 2 });
    expect(text('progress')).toContain('1 of 2');

    await push({
      kind: 'done',
      program: 'prog-1',
      done: 2,
      total: 2,
      cancelled: false,
      rows: ROWS,
    });

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
    expect(api.calls('POST', `${BASE}/prog-1/cancel`)).toHaveLength(1);
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
    const running: BookEditRunDto = {
      status: 'Running',
      done: 1,
      total: 2,
      rows: [],
      reason: null,
    };
    const done: BookEditRunDto = {
      status: 'Completed',
      done: 2,
      total: 2,
      rows: ROWS,
      reason: null,
    };
    let reads = 0;
    api.on('GET', `${BASE}/prog-1`, () => (reads++ === 0 ? running : done));
    await instruct();
    await analyze();

    await generate();
    expect(api.calls('POST', `${BASE}/prog-1/propose`)[0]?.body).toEqual({
      thinking: false,
      connectionId: null,
    });
    await settle(500);
    await dialog.rendered();
    expect(text('progress')).toContain('1 of 2');

    await settle(500);
    await dialog.rendered();
    expect(phase()).toBe('review');
    expect(rows()).toHaveLength(2);
  });

  it('applies the ticked rows as one book command, with hand edits included', async () => {
    await reachReview();

    // Untick the second row, then hand-edit the first.
    rows()[1]!.querySelector<HTMLInputElement>('input[type=checkbox]')!.click();
    await dialog.rendered();
    await openRow(0);
    type(proposed(), 'Chapter I');
    await dialog.rendered();

    expect(text('selected-count')).toBe('1 of 2 selected');
    expect(button('apply').textContent).toContain('Apply 1 selected');

    button('apply').click();
    await settle();

    expect(applied).toEqual([
      {
        type: 'ApplyBookEdits',
        edits: [{ kind: 'ChapterTitle', id: 'c1', newValue: 'Chapter I' }],
      },
    ]);
    expect(await result).toEqual({ applied: 1 });
    // Closing the dialog tells the host it is done with the plan.
    await settle();
    expect(api.calls('DELETE', `${BASE}/prog-1`)).toHaveLength(1);
  });

  it('keeps the hand edit in the model, not the row: a re-render shows it again', async () => {
    // Spec §7 risk 1: the draft lives in the review selection, so a row rendered afresh (here by
    // closing and reopening its detail) still carries it.
    await reachReview();
    await openRow(0);
    type(proposed(), 'Chapter I');
    await dialog.rendered();

    await openRow(0);
    expect(dialog.querySelector('[data-testid="proposed"]')).toBeNull();
    await openRow(0);

    expect(proposed().value).toBe('Chapter I');
    expect(rows()[0]!.querySelector('.r2m-status-chip')?.textContent).toContain('Edited');
  });

  it('keeps the dialog open with the reviewed rows when applying is refused', async () => {
    await reachReview();
    applyError = new ApiError(422, 'Unprocessable', 'the book moved on');

    button('apply').click();
    await settle();
    await dialog.rendered();

    expect(document.querySelector('r2m-edit-with-ai-dialog')).toBe(dialog);
    expect(phase()).toBe('review');
    expect(rows()).toHaveLength(2);
    expect(text('edit-error')).toContain('the book moved on');
  });

  it('re-asks the AI for one row with the sticky hint and takes the answer onto it', async () => {
    api.on('POST', `${BASE}/prog-1/propose-one`, row('c1', 'chapter one', 'Chapter 1'));
    await reachReview();
    await openRow(0);
    type(dialog.querySelector<HTMLInputElement>('[data-testid="hint"]')!, 'spell the number out');
    await dialog.rendered();

    button('retry-row').click();
    await settle();
    await dialog.rendered();

    expect(api.calls('POST', `${BASE}/prog-1/propose-one`)[0]?.body).toEqual({
      targetId: 'c1',
      hint: 'spell the number out',
      thinking: false,
    });
    expect(rows()[0]!.textContent).toContain('Chapter 1');
    expect(text('selected-count')).toBe('2 of 2 selected');
  });

  it('keeps the row as it was when a retry never gets an answer', async () => {
    api.on('POST', `${BASE}/prog-1/propose-one`, () => problem(502, 'Bad Gateway'));
    await reachReview();
    await openRow(0);
    type(proposed(), 'Chapter I');
    await dialog.rendered();

    button('retry-row').click();
    await settle();
    await dialog.rendered();

    // The hand edit and the AI's own proposal both survive; the failure sits on the row.
    expect(text('retry-error')).toContain('Asking the AI again failed');
    expect(proposed().value).toBe('Chapter I');
    expect(text('selected-count')).toBe('2 of 2 selected');
  });

  it('confirms before throwing hand edits away, and drops the session on Start over', async () => {
    await reachReview();
    await openRow(0);
    type(proposed(), 'Chapter I');
    await dialog.rendered();

    confirmed = false;
    button('start-over').click();
    await settle();
    await dialog.rendered();
    expect(phase()).toBe('review');

    confirmed = true;
    button('start-over').click();
    await settle();
    await dialog.rendered();

    expect(phase()).toBe('instruct');
    expect(api.calls('DELETE', `${BASE}/prog-1`)).toHaveLength(1);
  });

  it('Cancel closes with nothing', async () => {
    button('cancel').click();
    expect(await result).toBeNull();
  });
});
