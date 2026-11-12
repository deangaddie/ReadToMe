import {
  type ApiError,
  BookEditsApi,
  type BookEditItem,
  type BookEditRowDto,
  type Guid,
  type PlanBookEditResponse,
  toApiError,
} from '@app/api';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { type ReviewRow, ReviewSelection, planErrorMessage } from './review-model';

export type EditPhase = 'instruct' | 'plan' | 'proposing' | 'review';

/** How often the run is polled when the hub is not pushing to this tab, or has stopped. */
const POLL_MS = 400;

/** How often the run is checked anyway while the hub is pushing, in case the pushes stop. */
const PUSH_BACKSTOP_MS = 2000;

const DROPPED_REASON = 'The project was restored — run it again';

/** One run being followed; compared by identity, so a fresh run never matches an old one. */
interface RunWatch {
  readonly program: string;
  /** The hub connection the run was started for, the only one its job pushes to. */
  readonly pushTo: string | null;
  /** The hub is still reaching this tab; once off it stays off for the run. */
  pushing: boolean;
}

/**
 * One Edit with AI run, from Instruct to Review, for one open dialog: Instruct (what to change)
 * → Plan (what the host understood) → Proposing (the run, cancellable) → Review (hand-editable
 * rows, per-row retry, apply the ticked ones as one book command).
 *
 * It holds at most one server-side Edit session at a time, under the opaque `program` id the host
 * granted; a fresh Analyze or Start over replaces it, and {@link dispose} discards it. The plan
 * itself never reaches the browser, so a row is a value plus an id.
 *
 * The run is followed on the hub when this tab has a connection, and the session is polled behind
 * that as the answer of record. A Restore of the project drops the session whatever the phase, and
 * the workflow goes back to Instruct with the host's reason.
 *
 * View and form state (which row is open, the instruction text, the thinking switches, the hint)
 * belong to the dialog and arrive here as arguments.
 */
export class EditWithAiWorkflow {
  private readonly api = use(BookEditsApi);
  private readonly live = use(LiveService);
  private readonly preflight = use(Preflight);

  private readonly _phase = signal<EditPhase>('instruct');
  private readonly _error = signal<string | null>(null);
  private readonly _busy = signal(false);
  private readonly _plan = signal<PlanBookEditResponse | null>(null);
  private readonly _progress = signal({ done: 0, total: 0 });
  private readonly _selection = signal(ReviewSelection.empty());
  private readonly _retryingRowId = signal<Guid | null>(null);
  private readonly _retryError = signal<{ id: Guid; message: string } | null>(null);

  readonly phase = this._phase.asReadonly();
  /** What went wrong last, for the dialog banner; a retry's failure goes to {@link retryError}. */
  readonly error = this._error.asReadonly();
  /** A plan or an apply is in flight. */
  readonly busy = this._busy.asReadonly();
  readonly plan = this._plan.asReadonly();
  readonly progress = this._progress.asReadonly();
  /** The review rows, their hand edits and which are ticked. */
  readonly selection = this._selection.asReadonly();
  /** The row a retry is in flight for: busy state is row-local so the rest stays usable. */
  readonly retryingRowId = this._retryingRowId.asReadonly();
  /** A retry that never got an answer, shown on its own row rather than the dialog banner. */
  readonly retryError = this._retryError.asReadonly();

  readonly targetCount = computed(() => this._plan()?.targetCount ?? 0);
  readonly isLlmPlan = computed(() => this._plan()?.transform === 'Llm');

  private program: string | null = null;
  /**
   * Sessions a Restore dropped while a plan was on its way, by program id with the host's reason:
   * the drop can reach the hub before the plan's answer, when there is no program yet.
   */
  private droppedWhilePlanning: Map<string, string> | null = null;
  private readonly droppedSubscription: Unsubscribe;
  private runSubscription: Unsubscribe | null = null;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  /**
   * The run watch in force, replaced by the next run and cleared by an end, a drop, a discard or
   * {@link dispose}. A poll answer or hub message whose token is no longer this one belongs to a
   * watch that is over, and does nothing: the run's end has already been decided.
   */
  private currentWatch: RunWatch | null = null;
  private closed = false;

  /**
   * @param folder The project the session belongs to.
   * @param applyEdits Lands the edits as one book command; answers the refusal, or null.
   */
  constructor(
    private readonly folder: string,
    private readonly applyEdits: (edits: BookEditItem[]) => Promise<ApiError | null>,
  ) {
    // The plan and its rows were made against text the Restore replaced: back to Instruct, so the
    // instruction can be run again over the restored text.
    this.droppedSubscription = this.live.on('bookEdit', (message) => {
      if (message.kind !== 'Dropped') return;
      const reason = message.reason ?? DROPPED_REASON;
      if (message.program !== this.program) {
        this.droppedWhilePlanning?.set(message.program, reason);
        return;
      }
      this.program = null;
      this.stopWatching();
      this.back();
      this._error.set(reason);
    });
  }

  // ---- instruct ---------------------------------------------------------------------------------

  /** Plans the instruction; on a grant, replaces any session held and moves to Plan. */
  async analyze(instruction: string, thinking: boolean): Promise<void> {
    this._error.set(null);
    // A close while the preflight was out: nothing to plan for.
    if (!(await this.preflight.ensureReady('bookEdit')) || this.closed) return;

    this._busy.set(true);
    this.droppedWhilePlanning = new Map();
    try {
      const plan = await this.api.plan(this.folder, instruction.trim(), thinking);
      if (this.discardIfClosed(plan.program)) return;
      if (plan.status !== 'Ok' || !plan.program) {
        this._error.set(planErrorMessage(plan.status, plan.reason));
        return;
      }
      // Checked after the old session is dropped: a drop for the new one can land during that too.
      await this.dropSession();
      if (this.discardIfClosed(plan.program)) return;
      const droppedReason = this.droppedWhilePlanning.get(plan.program);
      if (droppedReason) {
        this._error.set(droppedReason);
        return;
      }
      this._plan.set(plan);
      this.program = plan.program;
      this._phase.set('plan');
    } catch (e) {
      this._error.set(toApiError(e).message);
    } finally {
      this.droppedWhilePlanning = null;
      this._busy.set(false);
    }
  }

  /** Back to Instruct, clearing the error and any rows. The session is kept for the next Analyze. */
  readonly back = (): void => {
    this.stopWatching();
    this._phase.set('instruct');
    this._error.set(null);
    this._selection.set(ReviewSelection.empty());
  };

  // ---- plan → proposing -------------------------------------------------------------------------

  /** Starts the proposal run and follows it to Review, or back to Plan when it fails. */
  async generate(thinking: boolean): Promise<void> {
    const program = this.program;
    if (!program) return;
    this._error.set(null);
    // Re-gate: the plan phase may have been minutes ago, and a GPU sweep for another task can have
    // stopped the LLM since. Deterministic transforms need no LLM at all.
    if (this.isLlmPlan() && !(await this.preflight.ensureReady('bookEdit'))) return;
    // A drop or a close while the preflight was out: there is no session to run any more.
    if (this.closed || this.program !== program) return;

    this._progress.set({ done: 0, total: this.targetCount() });
    this._phase.set('proposing');
    const connectionId = this.live.connectionId();
    const watch = this.watchRun(program, connectionId);
    try {
      await this.api.propose(this.folder, program, thinking, connectionId);
    } catch (e) {
      if (this.currentWatch === watch) this.failRun(toApiError(e).message);
    }
  }

  /** Asks the run to stop early; it lands its partial rows on its own. */
  async cancel(): Promise<void> {
    const program = this.program;
    if (!program) return;
    try {
      await this.api.cancel(this.folder, program);
    } catch (e) {
      this._error.set(toApiError(e).message);
    }
  }

  /** The run is over without rows: back to Plan with the reason, so the user can try again. */
  private failRun(reason: string | null | undefined): void {
    this.stopWatching();
    this._error.set(reason ?? 'The AI could not compute the proposals.');
    this._phase.set('plan');
  }

  /**
   * Follows the run to its end: the host pushes `bookEdit` messages to this tab's connection, and
   * the session is polled behind that as the answer of record.
   */
  private watchRun(program: string, pushTo: string | null): RunWatch {
    this.stopWatching();
    const watch: RunWatch = { program, pushTo, pushing: pushTo !== null };
    this.currentWatch = watch;
    if (watch.pushing) this.subscribe(watch);
    // The poll runs either way. The hub is the fast path, but a reconnect mints a new connection
    // id that the running job never learns, so its pushes would go nowhere and the run would sit
    // in Proposing for ever; the session is the one place the run's state is certain.
    this.poll(watch);
    return watch;
  }

  /**
   * Whether the job's pushes still reach this tab: the hub connection is still the one the run was
   * started for. A drop or a reconnect (which mints a new id) turns it off for good, since the job
   * never learns another id; re-read on every poll tick.
   */
  private stillPushing(watch: RunWatch): boolean {
    if (watch.pushing && this.live.connectionId() !== watch.pushTo) watch.pushing = false;
    return watch.pushing;
  }

  private subscribe(watch: RunWatch): void {
    this.runSubscription = this.live.on('bookEdit', (message) => {
      if (this.currentWatch !== watch || message.program !== watch.program) return;
      switch (message.kind) {
        case 'Progress':
          this._progress.set({
            done: message.done ?? 0,
            total: message.total ?? this.targetCount(),
          });
          break;
        case 'Done':
          this.finishRun(message.rows ?? []);
          break;
        case 'Failed':
          this.failRun(message.reason);
          break;
      }
    });
  }

  private poll(watch: RunWatch): void {
    this.pollTimer = setTimeout(
      () => {
        this.pollTimer = null;
        void (async () => {
          if (this.currentWatch !== watch) return;
          try {
            const run = await this.api.run(this.folder, watch.program);
            // The hub, a drop or a close can have ended the watch while the read was out.
            if (this.currentWatch !== watch) return;
            this._progress.set({ done: run.done, total: run.total });
            if (run.status === 'Completed' || run.status === 'Cancelled') {
              this.finishRun(run.rows);
              return;
            }
            if (run.status === 'Failed') {
              this.failRun(run.reason);
              return;
            }
          } catch (e) {
            if (this.currentWatch !== watch) return;
            // While the hub is pushing this is only the backstop: one failed read is not the run
            // failing, so keep watching. Without the hub it is the only signal, so say so.
            if (!this.stillPushing(watch)) {
              this.failRun(toApiError(e).message);
              return;
            }
          }
          this.poll(watch);
        })();
      },
      this.stillPushing(watch) ? PUSH_BACKSTOP_MS : POLL_MS,
    );
  }

  private finishRun(rows: readonly BookEditRowDto[]): void {
    this.stopWatching();
    // New rows, so a new selection: every appliable row starts ticked.
    this._selection.set(ReviewSelection.from(rows));
    this._phase.set('review');
  }

  private stopWatching(): void {
    this.currentWatch = null;
    this.runSubscription?.();
    this.runSubscription = null;
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  // ---- review -----------------------------------------------------------------------------------

  select(row: ReviewRow, selected: boolean): void {
    this._selection.update((s) => s.set(row, selected));
  }

  readonly selectAll = (): void => {
    this._selection.update((s) => s.selectAll());
  };

  readonly selectNone = (): void => {
    this._selection.update((s) => s.clear());
  };

  /** A hand edit of the row's proposed value. */
  setValue(row: ReviewRow, value: string): void {
    this._selection.update((s) => s.setValue(row, value));
  }

  /** Drops the row's hand edit, back to the AI's proposal. */
  revert(row: ReviewRow): void {
    this._selection.update((s) => s.revert(row));
  }

  /**
   * Re-asks the AI for this one row, steered by the hint (blank sends none). A failure lands on
   * the row ({@link retryError}) rather than the dialog banner.
   */
  async retry(row: ReviewRow, hint: string, thinking: boolean): Promise<void> {
    const program = this.program;
    if (!program) return;
    // Re-gate: minutes may have passed in review and a GPU sweep can have stopped the LLM.
    if (!(await this.preflight.ensureReady('bookEdit'))) return;

    this._retryingRowId.set(row.proposal.id);
    this._retryError.set(null);
    try {
      const proposal = await this.api.proposeOne(
        this.folder,
        program,
        row.proposal.id,
        hint.trim() || null,
        thinking,
      );
      // Only a real answer replaces the row. The AI's own "I could not do this one" arrives as a
      // Failed proposal and belongs on the row; a request that never got an answer does not —
      // it would throw away a good proposal, and the user's hand edit with it, over a blip.
      this._selection.update((s) => s.replaceProposal(row, proposal));
    } catch (e) {
      this._retryError.set({ id: row.proposal.id, message: toApiError(e).message });
    } finally {
      this._retryingRowId.set(null);
    }
  }

  /**
   * Lands every ticked row in one book mutation, so the book is never half-edited for a reader and
   * the reader behind the dialog converges on the whole program at once (ADR 0007). Answers how
   * many were applied, or null when nothing was: a refusal sets {@link error} and keeps the
   * reviewed rows, which may hold a long stretch of hand edits.
   */
  async apply(): Promise<number | null> {
    const edits = this._selection().toEdits();
    if (edits.length === 0) return null;

    this._busy.set(true);
    try {
      const failure = await this.applyEdits(edits);
      if (failure) {
        this._error.set(`Applying edits failed: ${failure.message}`);
        return null;
      }
      return edits.length;
    } finally {
      this._busy.set(false);
    }
  }

  /** Discards the session and goes back to Instruct. The dialog confirms first. */
  async startOver(): Promise<void> {
    await this.dropSession();
    this.back();
  }

  /** The dialog closed: stop listening and watching, and tell the host it can drop the session. */
  dispose(): void {
    if (this.closed) return;
    this.closed = true;
    this.droppedSubscription();
    this.stopWatching();
    // The host keeps the plan until it expires; say we are done with it so it goes now.
    if (this.program) void this.api.discard(this.folder, this.program).catch(() => undefined);
  }

  /**
   * A plan that comes back after {@link dispose} has no dialog to land in, and dispose had no
   * program to discard yet: hand the session back now rather than leave it until it expires, and
   * change no state. Answers whether the workflow is closed.
   */
  private discardIfClosed(program: string | null | undefined): boolean {
    if (!this.closed) return false;
    if (program) void this.api.discard(this.folder, program).catch(() => undefined);
    return true;
  }

  private async dropSession(): Promise<void> {
    const program = this.program;
    this.program = null;
    this.stopWatching();
    if (program) await this.api.discard(this.folder, program).catch(() => undefined);
  }
}
