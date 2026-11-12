import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import {
  ApiError,
  BookEditsApi,
  type BookEditItem,
  type BookEditMessage,
  type BookEditRowDto,
  type BookEditRunDto,
  type PlanBookEditResponse,
} from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { FakeTimers, settleMicrotasks } from '../../../../testing/fake-timers';
import { bookEditMessage } from '../../../../testing/live-messages';
import { EditWithAiWorkflow } from './edit-with-ai-workflow';

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

function row(id: string, oldValue: string, newValue: string | null): BookEditRowDto {
  return {
    kind: 'ChapterTitle',
    id,
    displayPath: `Book / ${oldValue}`,
    oldValue,
    newValue,
    status: newValue === null ? 'Failed' : 'Proposed',
    failureReason: newValue === null ? 'model returned nothing' : null,
    sources: [],
    canAskAgain: true,
  };
}

const ROWS = [row('c1', 'chapter one', 'Chapter One'), row('c2', 'chapter two', 'Chapter Two')];

const RUNNING: BookEditRunDto = { status: 'Running', done: 1, total: 2, rows: [], reason: null };
const COMPLETED: BookEditRunDto = {
  status: 'Completed',
  done: 2,
  total: 2,
  rows: ROWS,
  reason: null,
};

const done = (rows: BookEditRowDto[] = ROWS, program = 'prog-1') =>
  bookEditMessage({
    kind: 'Done',
    program,
    done: rows.length,
    total: rows.length,
    cancelled: false,
    rows,
  });

const dropped = (program = 'prog-1') =>
  bookEditMessage({
    kind: 'Dropped',
    program,
    reason: 'The project was restored — run it again',
  });

/**
 * The host as the workflow sees it: each call is recorded, and answers come from the handlers a
 * case sets. Defaults grant `prog-1`, start the run and keep it running.
 */
class FakeBookEditsApi {
  readonly calls: { method: string; args: unknown[] }[] = [];
  plan: (instruction: string, thinking: boolean) => Promise<PlanBookEditResponse> = async () =>
    PLAN;
  propose: () => Promise<void> = async () => undefined;
  run: () => Promise<BookEditRunDto> = async () => RUNNING;
  proposeOne: () => Promise<BookEditRowDto> = async () => row('c1', 'chapter one', 'Chapter 1');
  cancel: () => Promise<void> = async () => undefined;
  discard: (program: string) => Promise<void> = async () => undefined;

  readonly service = {
    plan: (_folder: string, instruction: string, thinking: boolean) =>
      this.record('plan', [instruction, thinking], () => this.plan(instruction, thinking)),
    propose: (_folder: string, program: string, thinking: boolean, connectionId: string | null) =>
      this.record('propose', [program, thinking, connectionId], () => this.propose()),
    run: (_folder: string, program: string) => this.record('run', [program], () => this.run()),
    proposeOne: (
      _folder: string,
      program: string,
      targetId: string,
      hint: string | null,
      thinking: boolean,
    ) => this.record('proposeOne', [program, targetId, hint, thinking], () => this.proposeOne()),
    cancel: (_folder: string, program: string) =>
      this.record('cancel', [program], () => this.cancel()),
    discard: (_folder: string, program: string) =>
      this.record('discard', [program], () => this.discard(program)),
  };

  called(method: string): unknown[][] {
    return this.calls.filter((c) => c.method === method).map((c) => c.args);
  }

  private record<T>(method: string, args: unknown[], answer: () => Promise<T>): Promise<T> {
    this.calls.push({ method, args });
    return answer();
  }
}

describe('EditWithAiWorkflow', () => {
  let api: FakeBookEditsApi;
  let messages: Emitter<BookEditMessage>;
  let connectionId: string | null;
  let ready: boolean;
  /** The preflight's answer; defaults to {@link ready}, a case can hold it open. */
  let ensureReady: () => Promise<boolean>;
  let applyError: ApiError | null;
  let applied: BookEditItem[][];
  let timers: FakeTimers;
  let workflow: EditWithAiWorkflow;

  beforeEach(() => {
    resetServices();
    timers = new FakeTimers().install();
    api = new FakeBookEditsApi();
    messages = new Emitter<BookEditMessage>();
    connectionId = 'conn-1';
    ready = true;
    applyError = null;
    applied = [];
    override(BookEditsApi, api.service as unknown as BookEditsApi);
    override(LiveService, {
      connectionId: () => connectionId,
      on: (_family: string, listener: (m: BookEditMessage) => void) => messages.subscribe(listener),
    } as unknown as LiveService);
    ensureReady = async () => ready;
    override(Preflight, { ensureReady: () => ensureReady() } as unknown as Preflight);
    workflow = new EditWithAiWorkflow('dracula', async (edits) => {
      applied.push(edits);
      return applyError;
    });
  });

  afterEach(() => {
    workflow.dispose();
    timers.restore();
  });

  const push = async (message: BookEditMessage) => {
    messages.emit(message);
    await settleMicrotasks();
  };

  async function reachProposing(): Promise<void> {
    await workflow.analyze('capitalise chapter titles', true);
    await workflow.generate(false);
  }

  async function reachReview(rows: BookEditRowDto[] = ROWS): Promise<void> {
    await reachProposing();
    await push(done(rows));
  }

  describe('analyze', () => {
    it('sends the trimmed instruction with its thinking flag and lands on Plan', async () => {
      await workflow.analyze('  rename the chapters ', false);

      expect(api.called('plan')).toEqual([['rename the chapters', false]]);
      expect(workflow.phase()).toBe('plan');
      expect(workflow.plan()).toEqual(PLAN);
      expect(workflow.busy()).toBe(false);
    });

    it('stays on Instruct and explains a refused plan', async () => {
      api.plan = async () => ({ ...PLAN, status: 'NoLlmConfigured', program: null });

      await workflow.analyze('capitalise chapter titles', true);

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toContain('No LLM server is configured');
    });

    it('explains a plan that matched nothing', async () => {
      api.plan = async () => ({ ...PLAN, status: 'NoTargets', program: null, targetCount: 0 });

      await workflow.analyze('capitalise chapter titles', true);

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toContain('No items in the book match');
    });

    it('stays on Instruct with the reason when the plan request fails', async () => {
      api.plan = async () => {
        throw new ApiError(409, 'Project is restoring');
      };

      await workflow.analyze('capitalise chapter titles', true);

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toContain('Project is restoring');
      expect(workflow.busy()).toBe(false);
    });

    it('asks nothing of the host when the preflight says no', async () => {
      ready = false;

      await workflow.analyze('capitalise chapter titles', true);

      expect(api.calls).toEqual([]);
      expect(workflow.phase()).toBe('instruct');
    });

    it('stays on Instruct when a Restore drops the plan before its answer arrives', async () => {
      api.plan = async () => {
        messages.emit(dropped());
        return PLAN;
      };

      await workflow.analyze('capitalise chapter titles', true);

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toBe('The project was restored — run it again');
      // The session is gone on the host: nothing to discard on close.
      workflow.dispose();
      expect(api.called('discard')).toEqual([]);
    });

    it('stays on Instruct when the drop lands while the earlier plan is being discarded', async () => {
      await workflow.analyze('capitalise chapter titles', true);
      workflow.back();
      api.plan = async () => ({ ...PLAN, program: 'prog-2' });
      api.discard = async (program) => {
        if (program === 'prog-1') messages.emit(dropped('prog-2'));
      };

      await workflow.analyze('capitalise chapter titles', true);

      expect(api.called('discard')).toEqual([['prog-1']]);
      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toBe('The project was restored — run it again');
    });

    it('replaces the session it held with the fresh one', async () => {
      await workflow.analyze('capitalise chapter titles', true);
      workflow.back();
      api.plan = async () => ({ ...PLAN, program: 'prog-2' });

      await workflow.analyze('rename the chapters', true);

      expect(api.called('discard')).toEqual([['prog-1']]);
      workflow.dispose();
      expect(api.called('discard')).toEqual([['prog-1'], ['prog-2']]);
    });
  });

  describe('generate', () => {
    it('starts the run for this connection and follows its progress on the hub', async () => {
      await workflow.analyze('capitalise chapter titles', true);

      await workflow.generate(true);

      expect(api.called('propose')).toEqual([['prog-1', true, 'conn-1']]);
      expect(workflow.phase()).toBe('proposing');
      expect(workflow.progress()).toEqual({ done: 0, total: 2 });

      await push(bookEditMessage({ kind: 'Progress', program: 'prog-1', done: 1, total: 2 }));
      expect(workflow.progress()).toEqual({ done: 1, total: 2 });
    });

    it('lands every appliable row ticked when the hub says Done', async () => {
      await reachReview([...ROWS, row('c3', 'chapter three', null)]);

      expect(workflow.phase()).toBe('review');
      expect(workflow.selection().rows).toHaveLength(3);
      expect(workflow.selection().count).toBe(2);
    });

    describe('a poll in flight when the hub says Done', () => {
      let answer: (run: BookEditRunDto) => void;

      beforeEach(async () => {
        await reachProposing();
        api.run = () => new Promise((resolve) => (answer = resolve));
        await timers.advance(2000);
        expect(api.called('run')).toHaveLength(1);
        await push(done());
        expect(workflow.phase()).toBe('review');
      });

      it('resolving Completed leaves the rows and their hand edits alone', async () => {
        workflow.setValue(workflow.selection().rows[0]!, 'Chapter I');
        workflow.select(workflow.selection().rows[1]!, false);
        const selection = workflow.selection();

        answer(COMPLETED);
        await settleMicrotasks();

        expect(workflow.selection()).toBe(selection);
        expect(workflow.selection().rows[0]!.userValue).toBe('Chapter I');
        expect(workflow.selection().count).toBe(1);
        expect(workflow.phase()).toBe('review');
        expect(timers.pending).toBe(0);
      });

      it('resolving Running arms no further poll', async () => {
        const progress = workflow.progress();

        answer(RUNNING);
        await settleMicrotasks();

        expect(timers.pending).toBe(0);
        expect(workflow.progress()).toBe(progress);
        await timers.advance(10_000);
        expect(api.called('run')).toHaveLength(1);
      });
    });

    it('only checks the run behind the hub every two seconds, and rides out a failed check', async () => {
      await reachProposing();
      api.run = async () => {
        throw new ApiError(502, 'Bad Gateway');
      };

      await timers.advance(1999);
      expect(api.called('run')).toHaveLength(0);
      await timers.advance(1);
      expect(api.called('run')).toHaveLength(1);
      expect(workflow.phase()).toBe('proposing');
      expect(workflow.error()).toBeNull();

      await timers.advance(2000);
      expect(api.called('run')).toHaveLength(2);
    });

    it('ignores a run that belongs to another plan', async () => {
      await reachProposing();

      await push(done(ROWS, 'other'));

      expect(workflow.phase()).toBe('proposing');
    });

    it('goes back to Plan with the reason when the hub says Failed', async () => {
      await reachProposing();

      await push(
        bookEditMessage({ kind: 'Failed', program: 'prog-1', reason: 'the model went away' }),
      );

      expect(workflow.phase()).toBe('plan');
      expect(workflow.error()).toBe('the model went away');
    });

    it('goes back to Plan when starting the run fails', async () => {
      api.propose = async () => {
        throw new ApiError(409, 'A run is already in flight');
      };
      await workflow.analyze('capitalise chapter titles', true);

      await workflow.generate(false);

      expect(workflow.phase()).toBe('plan');
      expect(workflow.error()).toContain('A run is already in flight');
      await timers.advance(10_000);
      expect(api.called('run')).toEqual([]);
    });

    it('re-gates an LLM plan, and stays on Plan when the preflight says no', async () => {
      await workflow.analyze('capitalise chapter titles', true);
      ready = false;

      await workflow.generate(false);

      expect(workflow.phase()).toBe('plan');
      expect(api.called('propose')).toEqual([]);
    });

    it('runs a deterministic plan without asking the preflight', async () => {
      api.plan = async () => ({ ...PLAN, transform: 'ChangeCase' });
      await workflow.analyze('capitalise chapter titles', true);
      ready = false;

      await workflow.generate(false);

      expect(workflow.phase()).toBe('proposing');
    });

    it('keeps the rows a cancelled run computed', async () => {
      await reachProposing();

      await workflow.cancel();
      expect(api.called('cancel')).toEqual([['prog-1']]);
      await push(
        bookEditMessage({
          kind: 'Done',
          program: 'prog-1',
          done: 1,
          total: 2,
          cancelled: true,
          rows: [ROWS[0]!],
        }),
      );

      expect(workflow.phase()).toBe('review');
      expect(workflow.selection().rows).toHaveLength(1);
    });
  });

  describe('without a hub connection', () => {
    beforeEach(() => {
      connectionId = null;
    });

    it('polls the run every 400 ms and lands its rows', async () => {
      let reads = 0;
      api.run = async () => (reads++ === 0 ? RUNNING : COMPLETED);
      await reachProposing();
      expect(api.called('propose')).toEqual([['prog-1', false, null]]);

      await timers.advance(400);
      expect(workflow.progress()).toEqual({ done: 1, total: 2 });
      expect(workflow.phase()).toBe('proposing');

      await timers.advance(400);
      expect(workflow.phase()).toBe('review');
      expect(workflow.selection().rows).toHaveLength(2);
    });

    it('goes back to Plan when the run reads Failed', async () => {
      api.run = async () => ({ ...RUNNING, status: 'Failed', reason: 'the model went away' });
      await reachProposing();

      await timers.advance(400);

      expect(workflow.phase()).toBe('plan');
      expect(workflow.error()).toBe('the model went away');
    });

    it('goes back to Plan when a poll fails: it is the only signal', async () => {
      api.run = async () => {
        throw new ApiError(502, 'Bad Gateway');
      };
      await reachProposing();

      await timers.advance(400);

      expect(workflow.phase()).toBe('plan');
      expect(workflow.error()).toContain('Bad Gateway');
    });
  });

  describe('when the hub stops reaching the run mid-run', () => {
    it('drops the check to every 400 ms once the hub drops', async () => {
      await reachProposing();
      await timers.advance(2000);
      expect(api.called('run')).toHaveLength(1);

      connectionId = null;
      await timers.advance(2000);
      expect(api.called('run')).toHaveLength(2);
      await timers.advance(400);
      expect(api.called('run')).toHaveLength(3);
    });

    it('drops the check to every 400 ms once the hub reconnects with a new id', async () => {
      await reachProposing();
      connectionId = 'conn-2';

      await timers.advance(2000);
      expect(api.called('run')).toHaveLength(1);
      await timers.advance(400);
      expect(api.called('run')).toHaveLength(2);
    });

    it('fails the run on one failed read after the switch', async () => {
      await reachProposing();
      connectionId = null;
      api.run = async () => {
        throw new ApiError(502, 'Bad Gateway');
      };

      await timers.advance(2000);

      expect(workflow.phase()).toBe('plan');
      expect(workflow.error()).toContain('Bad Gateway');
    });

    it('stays on 400 ms when the old id comes back', async () => {
      await reachProposing();
      connectionId = null;
      await timers.advance(2000);
      connectionId = 'conn-1';

      await timers.advance(400);
      expect(api.called('run')).toHaveLength(2);
      await timers.advance(400);
      expect(api.called('run')).toHaveLength(3);
    });

    it('keeps 400 ms when a hub connects mid-run with none at propose', async () => {
      connectionId = null;
      await reachProposing();
      await timers.advance(400);
      expect(api.called('run')).toHaveLength(1);

      connectionId = 'conn-1';
      await timers.advance(400);
      expect(api.called('run')).toHaveLength(2);
    });
  });

  describe('a Restore drops the session', () => {
    it('in Review: back to Instruct with no rows, and nothing to discard', async () => {
      await reachReview();

      await push(dropped());

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toBe('The project was restored — run it again');
      expect(workflow.selection().rows).toEqual([]);
      workflow.dispose();
      expect(api.called('discard')).toEqual([]);
    });

    it('while proposing: stops watching the run', async () => {
      await reachProposing();

      await push(dropped());

      expect(workflow.phase()).toBe('instruct');
      await timers.advance(10_000);
      expect(api.called('run')).toEqual([]);
    });

    it('while a poll is in flight: its late answer changes nothing', async () => {
      await reachProposing();
      let answer!: (run: BookEditRunDto) => void;
      api.run = () => new Promise((resolve) => (answer = resolve));
      await timers.advance(2000);
      await push(dropped());

      answer(COMPLETED);
      await settleMicrotasks();

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.selection().rows).toEqual([]);
      expect(workflow.error()).toBe('The project was restored — run it again');
      expect(timers.pending).toBe(0);
    });

    it('while a poll is in flight that then fails without the hub: the reason stays', async () => {
      connectionId = null;
      await reachProposing();
      let fail!: (e: unknown) => void;
      api.run = () => new Promise((_, reject) => (fail = reject));
      await timers.advance(400);
      await push(dropped());

      fail(new ApiError(502, 'Bad Gateway'));
      await settleMicrotasks();

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toBe('The project was restored — run it again');
    });

    it('while the run is starting, then the start fails: stays on Instruct', async () => {
      await workflow.analyze('capitalise chapter titles', true);
      api.propose = async () => {
        messages.emit(dropped());
        throw new ApiError(409, 'Project is restoring');
      };

      await workflow.generate(false);

      expect(workflow.phase()).toBe('instruct');
      expect(workflow.error()).toBe('The project was restored — run it again');
    });

    it('of another session: ignored', async () => {
      await reachReview();

      await push(dropped('other'));

      expect(workflow.phase()).toBe('review');
    });
  });

  describe('retry', () => {
    it('re-asks the AI for the row with the trimmed hint and takes the answer onto it', async () => {
      await reachReview();
      const first = workflow.selection().rows[0]!;
      workflow.setValue(first, 'Chapter I');

      await workflow.retry(workflow.selection().rows[0]!, ' spell it out ', true);

      expect(api.called('proposeOne')).toEqual([['prog-1', 'c1', 'spell it out', true]]);
      const retried = workflow.selection().rows[0]!;
      expect(retried.proposal.newValue).toBe('Chapter 1');
      expect(retried.userValue).toBeNull();
      expect(workflow.retryingRowId()).toBeNull();
    });

    it('sends no hint for a blank one', async () => {
      await reachReview();

      await workflow.retry(workflow.selection().rows[0]!, '   ', false);

      expect(api.called('proposeOne')).toEqual([['prog-1', 'c1', null, false]]);
    });

    it('marks the row retrying while the answer is on its way', async () => {
      await reachReview();
      let answer!: (r: BookEditRowDto) => void;
      api.proposeOne = () => new Promise((resolve) => (answer = resolve));

      const retrying = workflow.retry(workflow.selection().rows[1]!, '', false);
      await settleMicrotasks();
      expect(workflow.retryingRowId()).toBe('c2');

      answer(row('c2', 'chapter two', 'Chapter 2'));
      await retrying;
      expect(workflow.retryingRowId()).toBeNull();
    });

    it('lands an error on its row and keeps the row as it was', async () => {
      api.proposeOne = async () => {
        throw new ApiError(502, 'Bad Gateway');
      };
      await reachReview();
      workflow.setValue(workflow.selection().rows[0]!, 'Chapter I');

      await workflow.retry(workflow.selection().rows[0]!, '', false);

      expect(workflow.retryError()).toEqual({ id: 'c1', message: 'Bad Gateway' });
      expect(workflow.error()).toBeNull();
      expect(workflow.selection().rows[0]!.userValue).toBe('Chapter I');
      expect(workflow.selection().count).toBe(2);
    });

    it('asks nothing when the preflight says no', async () => {
      await reachReview();
      ready = false;

      await workflow.retry(workflow.selection().rows[0]!, '', false);

      expect(api.called('proposeOne')).toEqual([]);
    });
  });

  describe('apply', () => {
    it('applies the ticked rows, hand edits included, and answers how many', async () => {
      await reachReview();
      workflow.select(workflow.selection().rows[1]!, false);
      workflow.setValue(workflow.selection().rows[0]!, 'Chapter I');

      expect(await workflow.apply()).toBe(1);

      expect(applied).toEqual([[{ kind: 'ChapterTitle', id: 'c1', newValue: 'Chapter I' }]]);
    });

    it('keeps the rows and says why when applying is refused', async () => {
      await reachReview();
      applyError = new ApiError(422, 'the book moved on');

      expect(await workflow.apply()).toBeNull();

      expect(workflow.phase()).toBe('review');
      expect(workflow.selection().rows).toHaveLength(2);
      expect(workflow.error()).toBe('Applying edits failed: the book moved on');
      expect(workflow.busy()).toBe(false);
    });

    it('applies nothing when nothing is ticked', async () => {
      await reachReview();
      workflow.selectNone();

      expect(await workflow.apply()).toBeNull();
      expect(applied).toEqual([]);
    });
  });

  describe('start over', () => {
    it('discards the session and goes back to Instruct with no rows', async () => {
      await reachReview();

      await workflow.startOver();

      expect(api.called('discard')).toEqual([['prog-1']]);
      expect(workflow.phase()).toBe('instruct');
      expect(workflow.selection().rows).toEqual([]);
    });
  });

  describe('dispose', () => {
    it('discards the session while proposing and stops watching the run', async () => {
      await reachProposing();

      workflow.dispose();

      expect(api.called('discard')).toEqual([['prog-1']]);
      await timers.advance(10_000);
      expect(api.called('run')).toEqual([]);
      await push(done());
      expect(workflow.phase()).toBe('proposing');
    });

    it('while the run is being gated: starts no run and watches nothing', async () => {
      await workflow.analyze('capitalise chapter titles', true);
      let gate!: (ok: boolean) => void;
      ensureReady = () => new Promise((resolve) => (gate = resolve));

      const generating = workflow.generate(false);
      await settleMicrotasks();
      workflow.dispose();
      gate(true);
      await generating;

      expect(api.called('propose')).toEqual([]);
      expect(timers.pending).toBe(0);
    });

    it('while the plan is on its way: discards the plan it grants and stays on Instruct', async () => {
      let answer!: (plan: PlanBookEditResponse) => void;
      api.plan = () => new Promise((resolve) => (answer = resolve));

      const analyzing = workflow.analyze('capitalise chapter titles', true);
      await settleMicrotasks();
      workflow.dispose();
      answer(PLAN);
      await analyzing;

      expect(api.called('discard')).toEqual([['prog-1']]);
      expect(workflow.phase()).toBe('instruct');
      expect(workflow.plan()).toBeNull();
    });

    it('while the earlier session is being discarded for a fresh plan: discards both', async () => {
      await workflow.analyze('capitalise chapter titles', true);
      workflow.back();
      api.plan = async () => ({ ...PLAN, program: 'prog-2' });
      let discarded!: () => void;
      api.discard = (program) =>
        program === 'prog-1' ? new Promise((resolve) => (discarded = resolve)) : Promise.resolve();

      const analyzing = workflow.analyze('rename the chapters', true);
      await settleMicrotasks();
      workflow.dispose();
      discarded();
      await analyzing;

      expect(api.called('discard')).toEqual([['prog-1'], ['prog-2']]);
      expect(workflow.phase()).toBe('instruct');
      expect(workflow.plan()).toEqual(PLAN);
    });

    it('while Analyze is being gated: plans nothing', async () => {
      let gate!: (ok: boolean) => void;
      ensureReady = () => new Promise((resolve) => (gate = resolve));

      const analyzing = workflow.analyze('capitalise chapter titles', true);
      await settleMicrotasks();
      workflow.dispose();
      gate(true);
      await analyzing;

      expect(api.calls).toEqual([]);
      expect(workflow.busy()).toBe(false);
    });

    it('discards nothing when no session was granted', () => {
      workflow.dispose();

      expect(api.called('discard')).toEqual([]);
    });
  });
});
