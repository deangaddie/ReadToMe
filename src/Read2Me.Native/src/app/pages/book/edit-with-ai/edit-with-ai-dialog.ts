import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import {
  BookEditsApi,
  type BookEditRow,
  type Guid,
  type PlanBookEditResponse,
  toApiError,
} from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/dialogs';
import { icon, statusChip } from '@app/ui/partials';
import type { BookEditor } from '../book-editor';
import {
  type ReviewRow,
  type ReviewRowState,
  ReviewSelection,
  effectiveValue,
  isAppliable,
  isUserEdited,
  planErrorMessage,
  rowState,
} from './review-model';
import '@app/ui/stream-llm';
import editWithAiCss from './edit-with-ai-dialog.css' with { type: 'text' };

adoptStyles(editWithAiCss);

export interface EditWithAiDialogData {
  folder: string;
  /** The reader's editor, so the dialog applies through the same path the rows do. */
  editor: BookEditor;
}

export interface EditWithAiDialogResult {
  /** How many edits the host committed. */
  applied: number;
}

export type EditPhase = 'instruct' | 'plan' | 'proposing' | 'review';

const PHASES: { readonly id: EditPhase; readonly label: string }[] = [
  { id: 'instruct', label: 'Instruct' },
  { id: 'plan', label: 'Plan' },
  { id: 'proposing', label: 'Proposing' },
  { id: 'review', label: 'Review' },
];

/** How often the run is polled when there is no hub connection to push to. */
const POLL_MS = 400;

/** How often the run is checked anyway while the hub is pushing, in case the pushes stop. */
const PUSH_BACKSTOP_MS = 2000;

/** Long values are cut down to this in the table; the row's detail panel shows them in full. */
const PREVIEW_CHARS = 160;

const valueOf = (event: Event): string =>
  (event.target as HTMLInputElement | HTMLTextAreaElement).value;

const checkedOf = (event: Event): boolean => (event.target as HTMLInputElement).checked;

/** The per-row signals, read once per render (see {@link EditWithAiDialog.reviewBody}). */
interface ReviewView {
  openRowId: Guid | null;
  retryingRowId: Guid | null;
  retryError: { id: Guid; message: string } | null;
  hint: string;
  fixThinking: boolean;
  llm: boolean;
}

function preview(value: string | null | undefined): string {
  if (!value) return '';
  return value.length <= PREVIEW_CHARS ? value : `${value.slice(0, PREVIEW_CHARS)}…`;
}

/**
 * Edit with AI (research §3): Instruct (what to change, in plain language) → Plan (what the host
 * understood, how big the job is) → Proposing (one row per matched item, cancellable) → Review
 * (hand-editable rows, per-row retry, apply the ticked ones as one book command).
 *
 * The plan itself never reaches the browser: the host keeps it under the opaque `program` id this
 * dialog carries, so a row is a value plus an id and Apply is the ordinary `ApplyBookEdits`.
 *
 * The review rows are the book's only in-row editing (spec §7, risk 1): a row's draft lives in the
 * {@link ReviewSelection}, never in the textarea, so a row rendered afresh shows it again.
 */
export class EditWithAiDialog extends R2mElement {
  data!: EditWithAiDialogData;

  private readonly feed = use(LlmStreamFeed);
  private readonly api = use(BookEditsApi);
  private readonly live = use(LiveService);
  private readonly confirm = use(ConfirmService);
  private readonly preflight = use(Preflight);

  private readonly phase = signal<EditPhase>('instruct');
  private readonly instruction = signal('');
  private readonly error = signal<string | null>(null);
  private readonly busy = signal(false);
  private readonly showStream = signal(false);
  private readonly plan = signal<PlanBookEditResponse | null>(null);
  private readonly selection = signal(ReviewSelection.empty());
  private readonly openRowId = signal<Guid | null>(null);
  private readonly retryingRowId = signal<Guid | null>(null);
  /** A retry that never got an answer, shown on its own row rather than the dialog banner. */
  private readonly retryError = signal<{ id: Guid; message: string } | null>(null);
  /** Sticky for the dialog's lifetime: the same steer usually applies to the next row retried. */
  private readonly hint = signal('');
  /**
   * Thinking is chosen per phase because the two calls trade off differently: the plan is one call
   * where thinking is cheap, the fix runs once per batch/row where it dominates the runtime. Both
   * default off — thinking is opt-in, for when a plain run gets the edit wrong.
   */
  private readonly planThinking = signal(false);
  private readonly fixThinking = signal(false);
  private readonly progressDone = signal(0);
  private readonly progressTotal = signal(0);

  private readonly targetCount = computed(() => this.plan()?.targetCount ?? 0);
  private readonly isLlmPlan = computed(() => this.plan()?.transform === 'Llm');

  private program: string | null = null;
  private runSubscription: Unsubscribe | null = null;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private closed = false;

  protected override connected(): void {
    this.classList.add('edit-with-ai');
    this.feed.acquire();
    this.onDisconnect(() => {
      this.closed = true;
      this.stopWatching();
      this.feed.release();
      // The host keeps the plan until it expires; say we are done with it so it goes now.
      if (this.program)
        void this.api.discard(this.data.folder, this.program).catch(() => undefined);
    });
  }

  protected template() {
    const phase = this.phase();
    const error = this.error();
    return html`
      <h2 class="r2m-dialog__title">Edit with AI</h2>
      <div class="r2m-dialog__content edit" data-phase=${phase}>
        <ol class="edit__steps" aria-label="Progress">
          ${PHASES.map(
            (step) =>
              html`<li
                class="edit__step ${step.id === phase ? 'edit__step--current' : ''} ${
                  this.isBehind(step.id) ? 'edit__step--done' : ''
                }"
                data-step=${step.id}
                aria-current=${step.id === phase ? 'step' : nothing}
              >
                ${step.label}
              </li>`,
          )}
        </ol>

        ${
          error
            ? html`<p class="edit__error" role="alert" data-testid="edit-error">${error}</p>`
            : nothing
        }
        ${this.body(phase)}

        <!-- Always mounted, so it captures every request made while the dialog is open. -->
        <div class="edit__stream">
          <button
            type="button"
            class="r2m-button"
            data-action="toggle-stream"
            @click=${() => this.showStream.update((open) => !open)}
          >
            ${icon(this.showStream() ? 'expand_more' : 'expand_less')}
            ${this.showStream() ? 'Hide AI activity' : 'Show AI activity'}
          </button>
          <div class="edit__stream-body ${this.showStream() ? 'edit__stream-body--open' : ''}">
            <r2m-stream-llm .events=${this.feed.events()}></r2m-stream-llm>
          </div>
        </div>
      </div>
      <div class="r2m-dialog__actions">${this.actions(phase)}</div>
    `;
  }

  private body(phase: EditPhase) {
    switch (phase) {
      case 'instruct':
        return this.instructBody();
      case 'plan':
        return this.planBody();
      case 'proposing':
        return this.proposingBody();
      case 'review':
        return this.reviewBody();
    }
  }

  private instructBody() {
    const busy = this.busy();
    return html`<p class="edit__hint">
        Describe the change in plain language — e.g. “the first letter of the first paragraph of
        every chapter is missing, restore it”, “rename every chapter to ‘Chapter {n}’” or “make the
        all-caps headings sentence case”. Only titles and paragraph text can be edited.
      </p>
      <textarea
        class="edit__instruction"
        rows="4"
        aria-label="Instruction"
        data-testid="instruction"
        .value=${this.instruction()}
        ?disabled=${busy}
        @input=${(e: Event) => this.instruction.set(valueOf(e))}
      ></textarea>
      <label class="r2m-switch" data-testid="plan-thinking">
        <input
          type="checkbox"
          role="switch"
          .checked=${this.planThinking()}
          ?disabled=${busy}
          @change=${(e: Event) => this.planThinking.set(checkedOf(e))}
        />
        Let the AI think before planning
      </label>
      <span class="edit__hint">Slower, better on vague instructions</span>`;
  }

  private planBody() {
    const plan = this.plan();
    const count = this.targetCount();
    const requests = plan?.requestCount ?? 0;
    return html`<p class="edit__summary" data-testid="plan-summary">${plan?.summary}</p>
      <p class="edit__hint" data-testid="plan-counts">
        ${count} matching item${count === 1 ? '' : 's'} found.
        ${
          this.isLlmPlan()
            ? html`This sends ${requests} request${requests === 1 ? '' : 's'} to the LLM.`
            : nothing
        }
      </p>
      ${(plan?.warnings ?? []).map(
        (warning) =>
          html`<p class="edit__warning" role="alert" data-testid="plan-warning">${warning}</p>`,
      )}
      ${
        this.isLlmPlan()
          ? html`<!-- Separate from the plan toggle: thinking costs most of the generation time, and
                 this one is paid once per batch across the whole job. -->
              <label class="r2m-switch" data-testid="fix-thinking">
                <input
                  type="checkbox"
                  role="switch"
                  .checked=${this.fixThinking()}
                  @change=${(e: Event) => this.fixThinking.set(checkedOf(e))}
                />
                Let the AI think on each item
              </label>
              <span class="edit__hint">Slower, better on subtle edits</span>`
          : nothing
      }`;
  }

  private proposingBody() {
    const total = this.progressTotal();
    const done = this.progressDone();
    return html`<p class="edit__summary">${this.plan()?.summary}</p>
      ${
        total > 0
          ? html`<progress max=${total} value=${done} aria-label="Computing proposals"></progress>`
          : html`<progress aria-label="Computing proposals"></progress>`
      }
      <p class="edit__hint" data-testid="progress">${done} of ${total} computed…</p>`;
  }

  private reviewBody() {
    const selection = this.selection();
    // Read here, not in the row templates: `repeat` renders those at commit time, outside the
    // element's effect, so a signal read inside them would never re-render the dialog.
    const view: ReviewView = {
      openRowId: this.openRowId(),
      retryingRowId: this.retryingRowId(),
      retryError: this.retryError(),
      hint: this.hint(),
      fixThinking: this.fixThinking(),
      llm: this.isLlmPlan(),
    };
    return html`<p class="edit__hint">${this.plan()?.summary}</p>
      <div class="edit__select">
        <button type="button" class="r2m-button" data-action="select-all" @click=${this.selectAll}>
          Select all
        </button>
        <button
          type="button"
          class="r2m-button"
          data-action="select-none"
          @click=${this.selectNone}
        >
          Select none
        </button>
        <span class="edit__spacer"></span>
        <span class="edit__hint" data-testid="selected-count">
          ${selection.count} of ${selection.selectableCount} selected
        </span>
      </div>
      <p class="edit__hint">
        Click a row to read it in full and correct the AI’s suggestion by hand.
      </p>
      <div class="edit__rows">
        ${repeat(
          selection.rows,
          (row) => row.proposal.id,
          (row, i) => this.reviewRow(row, i, selection, view),
        )}
        ${
          selection.rows.length === 0
            ? html`<p class="edit__hint">The AI proposed nothing for this plan.</p>`
            : nothing
        }
      </div>`;
  }

  private reviewRow(row: ReviewRow, i: number, selection: ReviewSelection, view: ReviewView) {
    const state = rowState(row);
    const open = view.openRowId === row.proposal.id;
    return html`<div class="edit__row" data-row=${i}>
      <div class="edit__row-head">
        <input
          type="checkbox"
          class="edit__tick"
          .checked=${selection.isSelected(row)}
          ?disabled=${!isAppliable(row)}
          aria-label="Apply ${row.proposal.displayPath}"
          @change=${(e: Event) => this.select(row, checkedOf(e))}
        />
        <button
          class="edit__row-open"
          type="button"
          data-action="open-row"
          aria-expanded=${open ? 'true' : 'false'}
          @click=${() => this.toggleOpen(row)}
        >
          <span class="edit__where">${row.proposal.displayPath}</span>
          <span class="edit__old">${preview(row.proposal.oldValue)}</span>
          <span class="edit__new" data-state=${state}>${this.newValueText(row, state)}</span>
          ${
            isUserEdited(row)
              ? statusChip({ status: 'info', label: 'Edited', compact: true })
              : nothing
          }
        </button>
      </div>
      ${open ? this.rowDetail(row, i, state, view) : nothing}
    </div>`;
  }

  private newValueText(row: ReviewRow, state: ReviewRowState): string {
    switch (state) {
      case 'failed':
        return row.proposal.failureReason ?? '';
      case 'noChange':
        return '(no change)';
      case 'invalid':
        return 'Can’t be empty';
      default:
        return preview(effectiveValue(row));
    }
  }

  private rowDetail(row: ReviewRow, i: number, state: ReviewRowState, view: ReviewView) {
    const retrying = view.retryingRowId === row.proposal.id;
    const retryError = view.retryError;
    return html`<div class="edit__detail">
      <p class="edit__hint">Current</p>
      <p class="edit__current">${row.proposal.oldValue}</p>
      <label class="edit__hint" for="proposed-${i}">Proposed</label>
      <textarea
        class="edit__proposed"
        rows="5"
        id="proposed-${i}"
        data-testid="proposed"
        .value=${effectiveValue(row) ?? ''}
        ?disabled=${retrying}
        @input=${(e: Event) => this.setValue(row, valueOf(e))}
      ></textarea>
      ${
        state === 'invalid'
          ? html`<p class="edit__error" role="alert">
              This can’t be empty — AI edits never delete text.
            </p>`
          : nothing
      }
      ${
        view.llm
          ? html`<!-- Sticky for the dialog's lifetime: the same steer usually applies to the next
                 row retried. -->
              <label class="edit__hint" for="edit-hint">Hint for the AI (optional)</label>
              <input
                class="edit__hint-field"
                id="edit-hint"
                type="text"
                data-testid="hint"
                placeholder="e.g. keep the original spelling of names"
                .value=${view.hint}
                ?disabled=${retrying}
                @input=${(e: Event) => this.hint.set(valueOf(e))}
              />
              <label class="r2m-switch">
                <input
                  type="checkbox"
                  role="switch"
                  .checked=${view.fixThinking}
                  ?disabled=${retrying}
                  @change=${(e: Event) => this.fixThinking.set(checkedOf(e))}
                />
                Think before answering
              </label>`
          : nothing
      }
      ${
        retryError?.id === row.proposal.id
          ? html`<p class="edit__error" role="alert" data-testid="retry-error">
              Asking the AI again failed: ${retryError.message}. The proposal above is untouched.
            </p>`
          : nothing
      }
      <div class="edit__row-actions">
        <button
          type="button"
          class="r2m-button"
          data-action="revert-row"
          ?disabled=${!isUserEdited(row) || retrying}
          @click=${() => this.revert(row)}
        >
          Revert to AI
        </button>
        ${
          view.llm
            ? html`<button
                type="button"
                class="r2m-button r2m-button--stroked"
                data-action="retry-row"
                ?disabled=${view.retryingRowId !== null}
                @click=${() => void this.retry(row)}
              >
                ${icon('refresh')} Ask AI again
              </button>`
            : nothing
        }
        <span class="edit__spacer"></span>
        <button type="button" class="r2m-button" @click=${() => this.openRowId.set(null)}>
          Done
        </button>
      </div>
    </div>`;
  }

  private actions(phase: EditPhase) {
    const busy = this.busy();
    switch (phase) {
      case 'instruct':
        return html`<button
            type="button"
            class="r2m-button"
            data-action="cancel"
            @click=${this.close}
          >
            Cancel
          </button>
          <button
            type="button"
            class="r2m-button r2m-button--filled"
            data-action="analyze"
            ?disabled=${busy || this.instruction().trim() === ''}
            @click=${() => void this.analyze()}
          >
            Analyze
          </button>`;
      case 'plan':
        return html`<button
            type="button"
            class="r2m-button"
            data-action="back"
            @click=${this.backToInstruct}
          >
            Back
          </button>
          <button type="button" class="r2m-button" data-action="cancel" @click=${this.close}>
            Cancel
          </button>
          <button
            type="button"
            class="r2m-button r2m-button--filled"
            data-action="generate"
            @click=${() => void this.generate()}
          >
            Generate proposals
          </button>`;
      case 'proposing':
        return html`<button
          type="button"
          class="r2m-button edit__stop"
          data-action="cancel-proposing"
          @click=${() => void this.cancelProposing()}
        >
          Cancel
        </button>`;
      case 'review':
        return html`<button
            type="button"
            class="r2m-button"
            data-action="start-over"
            ?disabled=${busy}
            @click=${() => void this.startOver()}
          >
            Start over
          </button>
          <button
            type="button"
            class="r2m-button"
            data-action="close-review"
            ?disabled=${busy}
            @click=${() => void this.closeReview()}
          >
            Close
          </button>
          <button
            type="button"
            class="r2m-button r2m-button--filled"
            data-action="apply"
            ?disabled=${busy || this.selection().count === 0}
            @click=${() => void this.apply()}
          >
            Apply ${this.selection().count} selected
          </button>`;
    }
  }

  private isBehind(step: EditPhase): boolean {
    return PHASES.findIndex((p) => p.id === step) < PHASES.findIndex((p) => p.id === this.phase());
  }

  // ---- instruct ---------------------------------------------------------------------------------

  private async analyze(): Promise<void> {
    this.error.set(null);
    if (!(await this.preflight.ensureReady('bookEdit'))) return;

    this.busy.set(true);
    try {
      const plan = await this.api.plan(
        this.data.folder,
        this.instruction().trim(),
        this.planThinking(),
      );
      if (plan.status !== 'Ok' || !plan.program) {
        this.error.set(planErrorMessage(plan.status, plan.reason));
        return;
      }
      await this.dropSession();
      this.plan.set(plan);
      this.program = plan.program;
      this.phase.set('plan');
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  // ---- plan → proposing -------------------------------------------------------------------------

  private async generate(): Promise<void> {
    const program = this.program;
    if (!program) return;
    this.error.set(null);
    // Re-gate: the plan phase may have been minutes ago, and a GPU sweep for another task can have
    // stopped the LLM since. Deterministic transforms need no LLM at all.
    if (this.isLlmPlan() && !(await this.preflight.ensureReady('bookEdit'))) return;

    this.progressDone.set(0);
    this.progressTotal.set(this.targetCount());
    this.phase.set('proposing');
    this.watchRun(program);
    try {
      await this.api.propose(
        this.data.folder,
        program,
        this.fixThinking(),
        this.live.connectionId(),
      );
    } catch (e) {
      this.failRun(toApiError(e).message);
    }
  }

  /** The run is over without rows: back to Plan with the reason, so the user can try again. */
  private failRun(reason: string | null | undefined): void {
    this.stopWatching();
    this.error.set(reason ?? 'The AI could not compute the proposals.');
    this.phase.set('plan');
  }

  private async cancelProposing(): Promise<void> {
    const program = this.program;
    if (!program) return;
    // The run lands its partial rows on its own; this only asks it to stop early.
    try {
      await this.api.cancel(this.data.folder, program);
    } catch (e) {
      this.error.set(toApiError(e).message);
    }
  }

  /**
   * Follows the run to its end: the host pushes `bookEdit` messages to this tab's connection, and
   * the session is polled behind that as the answer of record.
   */
  private watchRun(program: string): void {
    this.stopWatching();
    if (this.live.connectionId() !== null) this.subscribe(program);
    // The poll runs either way. The hub is the fast path, but a reconnect mints a new connection
    // id that the running job never learns, so its pushes would go nowhere and the dialog would
    // sit in Proposing for ever; the session is the one place the run's state is certain.
    this.poll(program);
  }

  private subscribe(program: string): void {
    this.runSubscription = this.live.on('bookEdit', (message) => {
      if (message.program !== program) return;
      switch (message.kind) {
        case 'progress':
          this.progressDone.set(message.done ?? 0);
          this.progressTotal.set(message.total ?? this.targetCount());
          break;
        case 'done':
          this.finishRun(message.rows ?? []);
          break;
        case 'failed':
          this.failRun(message.reason);
          break;
      }
    });
  }

  private poll(program: string): void {
    const pushing = this.runSubscription !== null;
    this.pollTimer = setTimeout(
      () => {
        void (async () => {
          if (this.closed || this.phase() !== 'proposing') return;
          try {
            const run = await this.api.run(this.data.folder, program);
            this.progressDone.set(run.done);
            this.progressTotal.set(run.total);
            if (run.status === 'Completed' || run.status === 'Cancelled') {
              this.finishRun(run.rows);
              return;
            }
            if (run.status === 'Failed') {
              this.failRun(run.reason);
              return;
            }
          } catch (e) {
            // While the hub is pushing this is only the backstop: one failed read is not the run
            // failing, so keep watching. Without the hub it is the only signal, so say so.
            if (!pushing) {
              this.failRun(toApiError(e).message);
              return;
            }
          }
          this.poll(program);
        })();
      },
      pushing ? PUSH_BACKSTOP_MS : POLL_MS,
    );
  }

  private finishRun(rows: readonly BookEditRow[]): void {
    this.stopWatching();
    // New rows, so a new selection: every appliable row starts ticked.
    this.selection.set(ReviewSelection.from(rows));
    this.openRowId.set(null);
    this.phase.set('review');
  }

  private stopWatching(): void {
    this.runSubscription?.();
    this.runSubscription = null;
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  // ---- review -----------------------------------------------------------------------------------

  private select(row: ReviewRow, selected: boolean): void {
    this.selection.update((s) => s.set(row, selected));
  }

  private readonly selectAll = (): void => {
    this.selection.update((s) => s.selectAll());
  };

  private readonly selectNone = (): void => {
    this.selection.update((s) => s.clear());
  };

  private setValue(row: ReviewRow, value: string): void {
    this.selection.update((s) => s.setValue(row, value));
  }

  private revert(row: ReviewRow): void {
    this.selection.update((s) => s.revert(row));
  }

  private toggleOpen(row: ReviewRow): void {
    this.openRowId.update((open) => (open === row.proposal.id ? null : row.proposal.id));
  }

  /**
   * Re-asks the AI for this one row, steered by the sticky hint. Busy state is row-local so the
   * rest of the table stays usable, and a failure lands on the row rather than the dialog banner.
   */
  private async retry(row: ReviewRow): Promise<void> {
    const program = this.program;
    if (!program) return;
    // Re-gate: minutes may have passed in review and a GPU sweep can have stopped the LLM.
    if (!(await this.preflight.ensureReady('bookEdit'))) return;

    this.retryingRowId.set(row.proposal.id);
    this.retryError.set(null);
    try {
      const proposal = await this.api.proposeOne(
        this.data.folder,
        program,
        row.proposal.id,
        this.hint().trim() || null,
        this.fixThinking(),
      );
      // Only a real answer replaces the row. The AI's own "I could not do this one" arrives as a
      // Failed proposal and belongs on the row; a request that never got an answer does not —
      // it would throw away a good proposal, and the user's hand edit with it, over a blip.
      this.selection.update((s) => s.replaceProposal(row, proposal));
    } catch (e) {
      this.retryError.set({ id: row.proposal.id, message: toApiError(e).message });
    } finally {
      this.retryingRowId.set(null);
    }
  }

  /**
   * Lands every ticked row in one book mutation, so the book is never half-edited for a reader and
   * the reader behind this dialog converges on the whole program at once (ADR 0007).
   *
   * A refusal keeps the dialog open with the reviewed rows intact: the producer may have spent a
   * long time hand-editing them, and closing on a mutation that did not commit would throw that
   * away while looking like it had been applied.
   */
  private async apply(): Promise<void> {
    const edits = this.selection().toEdits();
    if (edits.length === 0) return;

    this.busy.set(true);
    try {
      const failure = await this.data.editor.tryExecute({ type: 'ApplyBookEdits', edits });
      if (failure) {
        this.error.set(`Applying edits failed: ${failure.message}`);
        return;
      }
      this.emit<EditWithAiDialogResult>('r2m-close', { applied: edits.length });
    } finally {
      this.busy.set(false);
    }
  }

  private async startOver(): Promise<void> {
    if (!(await this.confirmDiscard('Start over'))) return;
    await this.dropSession();
    this.backToInstruct();
  }

  private async closeReview(): Promise<void> {
    if (!(await this.confirmDiscard('Close'))) return;
    this.close();
  }

  private readonly backToInstruct = (): void => {
    this.phase.set('instruct');
    this.error.set(null);
    this.selection.set(ReviewSelection.empty());
    this.openRowId.set(null);
  };

  private readonly close = (): void => {
    this.emit('r2m-close', null);
  };

  /**
   * Hand edits live in the open dialog only — nothing is stored — so leaving the review screen
   * throws them away. Ask first when there are any.
   */
  private async confirmDiscard(action: string): Promise<boolean> {
    const edited = this.selection().handEditedCount;
    if (edited === 0) return true;
    return this.confirm.confirm({
      title: `${action}?`,
      message: `Discard ${edited} hand-edited proposal${edited === 1 ? '' : 's'}?`,
      confirmLabel: 'Discard',
      destructive: true,
    });
  }

  private async dropSession(): Promise<void> {
    const program = this.program;
    this.program = null;
    this.stopWatching();
    if (program) await this.api.discard(this.data.folder, program).catch(() => undefined);
  }
}
define('r2m-edit-with-ai-dialog', EditWithAiDialog);

/**
 * Opens the full-screen Edit with AI dialog over the reader's editor; resolves what it applied, or
 * null when it was closed.
 */
export async function openEditWithAiDialog(
  folder: string,
  editor: BookEditor,
): Promise<EditWithAiDialogResult | null> {
  const dialog = document.createElement('r2m-edit-with-ai-dialog');
  dialog.data = { folder, editor };
  return (
    (await openDialog<EditWithAiDialogResult | null>(dialog, { variant: 'fullscreen' })) ?? null
  );
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-edit-with-ai-dialog': EditWithAiDialog;
  }
}
