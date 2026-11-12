import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import type { Guid } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { ConfirmService } from '@app/ui/dialogs';
import { callout, icon, statusChip } from '@app/ui/partials';
import type { BookEditor } from '../book-editor';
import { type EditPhase, EditWithAiWorkflow } from './edit-with-ai-workflow';
import {
  type ReviewRow,
  type ReviewRowState,
  type ReviewSelection,
  askAgainBlockedReason,
  effectiveValue,
  isAppliable,
  isUserEdited,
  rowSources,
  rowState,
} from './review-model';
import { streamDisclosure } from '@app/ui/stream-disclosure';
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

const PHASES: { readonly id: EditPhase; readonly label: string }[] = [
  { id: 'instruct', label: 'Instruct' },
  { id: 'plan', label: 'Plan' },
  { id: 'proposing', label: 'Proposing' },
  { id: 'review', label: 'Review' },
];

/** Long values are cut down to this in the table; the row's detail panel shows them in full. */
const PREVIEW_CHARS = 160;

const valueOf = (event: Event): string =>
  (event.target as HTMLInputElement | HTMLTextAreaElement).value;

const checkedOf = (event: Event): boolean => (event.target as HTMLInputElement).checked;

/** Shown once above the proposed rows: the user is the only check on what the AI wrote. */
const REVIEW_WARNING = 'AI makes mistakes. Check every change carefully before you apply it.';

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
 * Edit with AI (research §3): renders the {@link EditWithAiWorkflow} that owns the run (Instruct →
 * Plan → Proposing → Review), and holds only view and form state: which row is open, the stream
 * disclosure, the instruction, the thinking switches and the hint. Leaving Review with hand edits
 * asks first.
 *
 * The review rows are the book's only in-row editing (spec §7, risk 1): a row's draft lives in the
 * workflow's {@link ReviewSelection}, never in the textarea, so a row rendered afresh shows it again.
 */
export class EditWithAiDialog extends R2mElement {
  data!: EditWithAiDialogData;

  private readonly feed = use(LlmStreamFeed);
  private readonly confirm = use(ConfirmService);

  /** Made on connect, from {@link data}; disposed on disconnect. */
  private workflow!: EditWithAiWorkflow;

  private readonly instruction = signal('');
  private readonly showStream = signal(false);
  private readonly openRowId = signal<Guid | null>(null);
  /** Sticky for the dialog's lifetime: the same steer usually applies to the next row retried. */
  private readonly hint = signal('');
  /**
   * Thinking is chosen per phase because the two calls trade off differently: the plan is one call
   * where thinking is cheap, the fix runs once per batch/row where it dominates the runtime. Both
   * default differently: the plan defaults on (the planner chooses between tokens and an LLM from the
   * sample, which a plain run gets wrong too often), the fix defaults off — thinking is opt-in there.
   */
  private readonly planThinking = signal(true);
  private readonly fixThinking = signal(false);

  protected override connected(): void {
    this.classList.add('edit-with-ai');
    this.feed.acquire();
    const { folder, editor } = this.data;
    const workflow = new EditWithAiWorkflow(folder, (edits) =>
      editor.tryExecute({ type: 'ApplyBookEdits', edits }),
    );
    this.workflow = workflow;
    // Rows land closed: whenever the workflow leaves Review, forget the open row.
    this.effect(() => {
      if (workflow.phase() !== 'review') this.openRowId.set(null);
    });
    this.onDisconnect(() => {
      workflow.dispose();
      this.feed.release();
    });
  }

  protected template() {
    const phase = this.workflow.phase();
    const error = this.workflow.error();
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
        ${streamDisclosure({
          feed: this.feed,
          open: this.showStream(),
          onToggle: () => this.showStream.update((open) => !open),
          cls: 'edit__stream',
        })}
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
    const busy = this.workflow.busy();
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
    const plan = this.workflow.plan();
    const count = this.workflow.targetCount();
    const requests = plan?.requestCount ?? 0;
    return html`<p class="edit__summary" data-testid="plan-summary">${plan?.summary}</p>
      <p class="edit__hint" data-testid="plan-counts">
        ${count} matching item${count === 1 ? '' : 's'} found.
        ${
          this.workflow.isLlmPlan()
            ? html`This sends ${requests} request${requests === 1 ? '' : 's'} to the LLM.`
            : nothing
        }
      </p>
      ${(plan?.warnings ?? []).map(
        (warning) =>
          html`<p class="edit__warning" role="alert" data-testid="plan-warning">${warning}</p>`,
      )}
      ${
        this.workflow.isLlmPlan()
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
    const { done, total } = this.workflow.progress();
    return html`<p class="edit__summary">${this.workflow.plan()?.summary}</p>
      ${
        total > 0
          ? html`<progress max=${total} value=${done} aria-label="Computing proposals"></progress>`
          : html`<progress aria-label="Computing proposals"></progress>`
      }
      <p class="edit__hint" data-testid="progress">${done} of ${total} computed…</p>`;
  }

  private reviewBody() {
    const selection = this.workflow.selection();
    // Read here, not in the row templates: `repeat` renders those at commit time, outside the
    // element's effect, so a signal read inside them would never re-render the dialog.
    const view: ReviewView = {
      openRowId: this.openRowId(),
      retryingRowId: this.workflow.retryingRowId(),
      retryError: this.workflow.retryError(),
      hint: this.hint(),
      fixThinking: this.fixThinking(),
      llm: this.workflow.isLlmPlan(),
    };
    return html`<p class="edit__hint">${this.workflow.plan()?.summary}</p>
      ${callout({ kind: 'warn', text: REVIEW_WARNING, testId: 'review-warning' })}
      <div class="edit__select">
        <button
          type="button"
          class="r2m-button"
          data-action="select-all"
          @click=${this.workflow.selectAll}
        >
          Select all
        </button>
        <button
          type="button"
          class="r2m-button"
          data-action="select-none"
          @click=${this.workflow.selectNone}
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
          @change=${(e: Event) => this.workflow.select(row, checkedOf(e))}
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
    const blocked = askAgainBlockedReason(row);
    return html`<div class="edit__detail">
      ${this.sourcesList(row)}
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
        @input=${(e: Event) => this.workflow.setValue(row, valueOf(e))}
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
          @click=${() => this.workflow.revert(row)}
        >
          Revert to AI
        </button>
        ${
          view.llm
            ? html`<button
                type="button"
                class="r2m-button r2m-button--stroked"
                data-action="retry-row"
                ?disabled=${view.retryingRowId !== null || blocked !== null}
                data-tooltip=${blocked ?? nothing}
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

  /** Label, name and value of each source the row read, above Current; nothing for a plan that reads none. */
  private sourcesList(row: ReviewRow) {
    const sources = rowSources(row);
    if (sources.length === 0) return nothing;
    return html`<p class="edit__hint">Sources</p>
      <ul class="edit__sources" data-testid="sources">
        ${sources.map(
          (s) =>
            html`<li>
              <span class="edit__source-label">${s.label} (${s.name})</span>
              <span class="edit__source-value">${s.value}</span>
            </li>`,
        )}
      </ul>`;
  }

  private actions(phase: EditPhase) {
    const busy = this.workflow.busy();
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
            @click=${this.workflow.back}
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
          @click=${() => void this.workflow.cancel()}
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
            ?disabled=${busy || this.workflow.selection().count === 0}
            @click=${() => void this.apply()}
          >
            Apply ${this.workflow.selection().count} selected
          </button>`;
    }
  }

  private isBehind(step: EditPhase): boolean {
    return (
      PHASES.findIndex((p) => p.id === step) <
      PHASES.findIndex((p) => p.id === this.workflow.phase())
    );
  }

  // ---- actions --------------------------------------------------------------------------------

  private analyze(): Promise<void> {
    return this.workflow.analyze(this.instruction(), this.planThinking());
  }

  private generate(): Promise<void> {
    return this.workflow.generate(this.fixThinking());
  }

  private toggleOpen(row: ReviewRow): void {
    this.openRowId.update((open) => (open === row.proposal.id ? null : row.proposal.id));
  }

  private retry(row: ReviewRow): Promise<void> {
    return this.workflow.retry(row, this.hint(), this.fixThinking());
  }

  private async apply(): Promise<void> {
    const applied = await this.workflow.apply();
    if (applied !== null) this.emit<EditWithAiDialogResult>('r2m-close', { applied });
  }

  private async startOver(): Promise<void> {
    if (!(await this.confirmDiscard('Start over'))) return;
    await this.workflow.startOver();
  }

  private async closeReview(): Promise<void> {
    if (!(await this.confirmDiscard('Close'))) return;
    this.close();
  }

  private readonly close = (): void => {
    this.emit('r2m-close', null);
  };

  /**
   * Hand edits live in the open dialog only — nothing is stored — so leaving the review screen
   * throws them away. Ask first when there are any.
   */
  private async confirmDiscard(action: string): Promise<boolean> {
    const edited = this.workflow.selection().handEditedCount;
    if (edited === 0) return true;
    return this.confirm.confirm({
      title: `${action}?`,
      message: `Discard ${edited} hand-edited proposal${edited === 1 ? '' : 's'}?`,
      confirmLabel: 'Discard',
      destructive: true,
    });
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
