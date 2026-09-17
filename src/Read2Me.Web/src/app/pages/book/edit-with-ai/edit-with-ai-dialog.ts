import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  OnDestroy,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import {
  BookEditRow,
  BookEditsApi,
  Guid,
  PlanBookEditResponse,
  toApiError,
} from '@app/api';
import { LiveService } from '@app/live';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { StreamLlm } from '@app/ui/stream-llm/stream-llm';
import { Subscription, firstValueFrom } from 'rxjs';
import { BookEditor } from '../book-editor';
import {
  ReviewRow,
  ReviewSelection,
  effectiveValue,
  isAppliable,
  isUserEdited,
  planErrorMessage,
  rowState,
} from './review-model';

export interface EditWithAiDialogData {
  folder: string;
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

/**
 * Edit with AI (research §3): Instruct (what to change, in plain language) → Plan (what the host
 * understood, how big the job is) → Proposing (one row per matched item, cancellable) → Review
 * (hand-editable rows, per-row retry, apply the ticked ones as one book command).
 *
 * The plan itself never reaches the browser: the host keeps it under the opaque `program` id this
 * dialog carries, so a row is a value plus an id and Apply is the ordinary `ApplyBookEdits`.
 */
@Component({
  selector: 'app-edit-with-ai-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressBarModule,
    MatSlideToggleModule,
    StatusChip,
    StreamLlm,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Edit with AI</h2>
    <mat-dialog-content class="edit" [attr.data-phase]="phase()">
      <ol class="edit__steps" aria-label="Progress">
        @for (step of phases; track step.id) {
          <li
            class="edit__step"
            [class.edit__step--current]="step.id === phase()"
            [class.edit__step--done]="isBehind(step.id)"
            [attr.data-step]="step.id"
            [attr.aria-current]="step.id === phase() ? 'step' : null"
          >
            {{ step.label }}
          </li>
        }
      </ol>

      @if (error(); as message) {
        <p class="edit__error" role="alert" data-testid="edit-error">{{ message }}</p>
      }

      @switch (phase()) {
        @case ('instruct') {
          <p class="edit__hint">
            Describe the change in plain language — e.g. “the first letter of the first paragraph of
            every chapter is missing, restore it” or “rename every chapter to ‘Chapter {{ '{' }}n{{ '}' }}’”.
            Only titles and paragraph text can be edited.
          </p>
          <textarea
            class="edit__instruction"
            rows="4"
            aria-label="Instruction"
            data-testid="instruction"
            [value]="instruction()"
            [disabled]="busy()"
            (input)="instruction.set(valueOf($event))"
          ></textarea>
          <mat-slide-toggle
            data-testid="plan-thinking"
            [checked]="planThinking()"
            [disabled]="busy()"
            (change)="planThinking.set($event.checked)"
          >
            Let the AI think before planning
          </mat-slide-toggle>
          <span class="edit__hint">Slower, better on vague instructions</span>
        }
        @case ('plan') {
          <p class="edit__summary" data-testid="plan-summary">{{ plan()?.summary }}</p>
          <p class="edit__hint" data-testid="plan-counts">
            {{ targetCount() }} matching item{{ targetCount() === 1 ? '' : 's' }} found.
            @if (isLlmPlan()) {
              This sends {{ plan()?.requestCount }} request{{
                plan()?.requestCount === 1 ? '' : 's'
              }}
              to the LLM.
            }
          </p>
          @for (warning of plan()?.warnings ?? []; track warning) {
            <p class="edit__warning" role="alert" data-testid="plan-warning">{{ warning }}</p>
          }
          @if (isLlmPlan()) {
            <!-- Separate from the plan toggle: thinking costs most of the generation time, and
                 this one is paid once per batch across the whole job. -->
            <mat-slide-toggle
              data-testid="fix-thinking"
              [checked]="fixThinking()"
              (change)="fixThinking.set($event.checked)"
            >
              Let the AI think on each item
            </mat-slide-toggle>
            <span class="edit__hint">Slower, better on subtle edits</span>
          }
        }
        @case ('proposing') {
          <p class="edit__summary">{{ plan()?.summary }}</p>
          <mat-progress-bar
            [mode]="progressTotal() > 0 ? 'determinate' : 'indeterminate'"
            [value]="progressPercent()"
            aria-label="Computing proposals"
          />
          <p class="edit__hint" data-testid="progress">
            {{ progressDone() }} of {{ progressTotal() }} computed…
          </p>
        }
        @case ('review') {
          <p class="edit__hint">{{ plan()?.summary }}</p>
          <div class="edit__select">
            <button mat-button type="button" data-action="select-all" (click)="selectAll()">
              Select all
            </button>
            <button mat-button type="button" data-action="select-none" (click)="selectNone()">
              Select none
            </button>
            <span class="edit__spacer"></span>
            <span class="edit__hint" data-testid="selected-count">
              {{ selection().count }} of {{ selection().selectableCount }} selected
            </span>
          </div>
          <p class="edit__hint">
            Click a row to read it in full and correct the AI’s suggestion by hand.
          </p>
          <div class="edit__rows">
            @for (row of selection().rows; track row.proposal.id; let i = $index) {
              <div class="edit__row" [attr.data-row]="i">
                <div class="edit__row-head">
                  <mat-checkbox
                    [checked]="selection().isSelected(row)"
                    [disabled]="!appliable(row)"
                    [aria-label]="'Apply ' + row.proposal.displayPath"
                    (change)="select(row, $event.checked)"
                  />
                  <button
                    class="edit__row-open"
                    type="button"
                    data-action="open-row"
                    [attr.aria-expanded]="openRowId() === row.proposal.id"
                    (click)="toggleOpen(row)"
                  >
                    <span class="edit__where">{{ row.proposal.displayPath }}</span>
                    <span class="edit__old">{{ preview(row.proposal.oldValue) }}</span>
                    <span class="edit__new" [attr.data-state]="state(row)">
                      @switch (state(row)) {
                        @case ('failed') {
                          {{ row.proposal.failureReason }}
                        }
                        @case ('noChange') {
                          (no change)
                        }
                        @case ('invalid') {
                          Can’t be empty
                        }
                        @default {
                          {{ preview(value(row)) }}
                        }
                      }
                    </span>
                    @if (edited(row)) {
                      <r2m-status-chip status="info" label="Edited" compact />
                    }
                  </button>
                </div>
                @if (openRowId() === row.proposal.id) {
                  <div class="edit__detail">
                    <p class="edit__hint">Current</p>
                    <p class="edit__current">{{ row.proposal.oldValue }}</p>
                    <label class="edit__hint" [attr.for]="'proposed-' + i">Proposed</label>
                    <textarea
                      class="edit__proposed"
                      rows="5"
                      [id]="'proposed-' + i"
                      data-testid="proposed"
                      [value]="value(row) ?? ''"
                      [disabled]="retryingRowId() === row.proposal.id"
                      (input)="setValue(row, valueOf($event))"
                    ></textarea>
                    @if (state(row) === 'invalid') {
                      <p class="edit__error" role="alert">
                        This can’t be empty — AI edits never delete text.
                      </p>
                    }
                    @if (isLlmPlan()) {
                      <!-- Sticky for the dialog's lifetime: the same steer usually applies to the
                           next row retried. -->
                      <label class="edit__hint" for="edit-hint">Hint for the AI (optional)</label>
                      <input
                        class="edit__hint-field"
                        id="edit-hint"
                        type="text"
                        data-testid="hint"
                        placeholder="e.g. keep the original spelling of names"
                        [value]="hint()"
                        [disabled]="retryingRowId() === row.proposal.id"
                        (input)="hint.set(valueOf($event))"
                      />
                      <mat-slide-toggle
                        [checked]="fixThinking()"
                        [disabled]="retryingRowId() === row.proposal.id"
                        (change)="fixThinking.set($event.checked)"
                      >
                        Think before answering
                      </mat-slide-toggle>
                    }
                    @if (retryError()?.id === row.proposal.id) {
                      <p class="edit__error" role="alert" data-testid="retry-error">
                        Asking the AI again failed: {{ retryError()?.message }}. The proposal above
                        is untouched.
                      </p>
                    }
                    <div class="edit__row-actions">
                      <button
                        mat-button
                        type="button"
                        data-action="revert-row"
                        [disabled]="!edited(row) || retryingRowId() === row.proposal.id"
                        (click)="revert(row)"
                      >
                        Revert to AI
                      </button>
                      @if (isLlmPlan()) {
                        <button
                          mat-stroked-button
                          type="button"
                          data-action="retry-row"
                          [disabled]="retryingRowId() !== null"
                          (click)="retry(row)"
                        >
                          <mat-icon>refresh</mat-icon> Ask AI again
                        </button>
                      }
                      <span class="edit__spacer"></span>
                      <button mat-button type="button" (click)="openRowId.set(null)">Done</button>
                    </div>
                  </div>
                }
              </div>
            }
            @if (selection().rows.length === 0) {
              <p class="edit__hint">The AI proposed nothing for this plan.</p>
            }
          </div>
        }
      }

      <!-- Always mounted, so it captures every request made while the dialog is open. -->
      <div class="edit__stream">
        <button
          mat-button
          type="button"
          data-action="toggle-stream"
          (click)="showStream.set(!showStream())"
        >
          <mat-icon>{{ showStream() ? 'expand_more' : 'expand_less' }}</mat-icon>
          {{ showStream() ? 'Hide AI activity' : 'Show AI activity' }}
        </button>
        <div class="edit__stream-body" [class.edit__stream-body--open]="showStream()">
          <r2m-stream-llm [events]="feed.events()" />
        </div>
      </div>
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      @switch (phase()) {
        @case ('instruct') {
          <button mat-button type="button" data-action="cancel" (click)="close()">Cancel</button>
          <button
            mat-flat-button
            type="button"
            data-action="analyze"
            [disabled]="busy() || instruction().trim() === ''"
            (click)="analyze()"
          >
            Analyze
          </button>
        }
        @case ('plan') {
          <button mat-button type="button" data-action="back" (click)="backToInstruct()">
            Back
          </button>
          <button mat-button type="button" data-action="cancel" (click)="close()">Cancel</button>
          <button mat-flat-button type="button" data-action="generate" (click)="generate()">
            Generate proposals
          </button>
        }
        @case ('proposing') {
          <button
            mat-button
            class="edit__stop"
            type="button"
            data-action="cancel-proposing"
            (click)="cancelProposing()"
          >
            Cancel
          </button>
        }
        @case ('review') {
          <button mat-button type="button" data-action="start-over" [disabled]="busy()" (click)="startOver()">
            Start over
          </button>
          <button mat-button type="button" data-action="close-review" [disabled]="busy()" (click)="closeReview()">
            Close
          </button>
          <button
            mat-flat-button
            type="button"
            data-action="apply"
            [disabled]="busy() || selection().count === 0"
            (click)="apply()"
          >
            Apply {{ selection().count }} selected
          </button>
        }
      }
    </mat-dialog-actions>
  `,
  styles: `
    .edit {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      min-height: 60vh;
    }
    .edit__steps {
      display: flex;
      gap: var(--r2m-space-2);
      list-style: none;
      margin: 0;
      padding: 0;
      flex-wrap: wrap;
    }
    .edit__step {
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border-radius: var(--r2m-radius-pill);
      border: 1px solid var(--r2m-outline);
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
    .edit__step--current {
      border-color: var(--r2m-primary);
      color: var(--r2m-text);
      font-weight: 600;
    }
    .edit__step--done {
      color: var(--r2m-text);
    }
    .edit__hint {
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
      margin: 0;
    }
    .edit__summary {
      margin: 0;
    }
    .edit__error {
      color: var(--r2m-status-error);
      margin: 0;
    }
    .edit__warning {
      color: var(--r2m-status-warn);
      margin: 0;
    }
    .edit__instruction,
    .edit__proposed,
    .edit__hint-field {
      font: inherit;
      width: 100%;
      padding: var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-sm);
      background: var(--r2m-surface);
      color: var(--r2m-text);
      resize: vertical;
    }
    .edit__select {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .edit__spacer {
      flex: 1 1 auto;
    }
    .edit__rows {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
      overflow-y: auto;
    }
    .edit__row {
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      padding: var(--r2m-space-1) var(--r2m-space-2);
    }
    .edit__row-head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .edit__row-open {
      flex: 1 1 auto;
      min-width: 0;
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1fr) auto;
      gap: var(--r2m-space-2);
      align-items: center;
      text-align: left;
      font: inherit;
      color: inherit;
      background: none;
      border: 0;
      padding: var(--r2m-space-1) 0;
      cursor: pointer;
    }
    .edit__where {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .edit__old {
      text-decoration: line-through;
      opacity: 0.7;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .edit__new {
      color: var(--r2m-status-ok);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .edit__new[data-state='failed'],
    .edit__new[data-state='invalid'] {
      color: var(--r2m-status-error);
    }
    .edit__new[data-state='noChange'] {
      color: var(--r2m-text-muted);
    }
    .edit__detail {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
      padding: var(--r2m-space-2) 0;
      border-top: 1px solid var(--r2m-outline);
    }
    .edit__current {
      margin: 0;
      max-height: 18vh;
      overflow-y: auto;
      white-space: pre-wrap;
    }
    .edit__row-actions {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .edit__stream {
      margin-top: auto;
      border-top: 1px solid var(--r2m-outline);
      padding-top: var(--r2m-space-1);
    }
    .edit__stream-body {
      height: 0;
      overflow: hidden;
      transition: height 0.15s ease;
    }
    .edit__stream-body--open {
      height: 32vh;
    }
    .edit__stop {
      color: var(--r2m-status-error);
    }
  `,
})
export class EditWithAiDialog implements OnDestroy {
  protected readonly data = inject<EditWithAiDialogData>(MAT_DIALOG_DATA);
  protected readonly feed = inject(LlmStreamFeed);
  private readonly ref =
    inject<MatDialogRef<EditWithAiDialog, EditWithAiDialogResult>>(MatDialogRef);
  private readonly api = inject(BookEditsApi);
  private readonly live = inject(LiveService);
  private readonly editor = inject(BookEditor);
  private readonly confirm = inject(ConfirmService);
  private readonly preflight = inject(Preflight);

  protected readonly phases = PHASES;
  protected readonly phase = signal<EditPhase>('instruct');
  protected readonly instruction = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly showStream = signal(false);
  protected readonly plan = signal<PlanBookEditResponse | null>(null);
  protected readonly selection = signal(ReviewSelection.empty());
  protected readonly openRowId = signal<Guid | null>(null);
  protected readonly retryingRowId = signal<Guid | null>(null);
  /** A retry that never got an answer, shown on its own row rather than the dialog banner. */
  protected readonly retryError = signal<{ id: Guid; message: string } | null>(null);
  /** Sticky for the dialog's lifetime: the same steer usually applies to the next row retried. */
  protected readonly hint = signal('');
  /**
   * Thinking is chosen per phase because the two calls trade off differently: the plan is one call
   * where thinking is cheap, the fix runs once per batch/row where it dominates the runtime. Both
   * default off — thinking is opt-in, for when a plain run gets the edit wrong.
   */
  protected readonly planThinking = signal(false);
  protected readonly fixThinking = signal(false);
  protected readonly progressDone = signal(0);
  protected readonly progressTotal = signal(0);

  protected readonly targetCount = computed(() => this.plan()?.targetCount ?? 0);
  protected readonly isLlmPlan = computed(() => this.plan()?.transform === 'Llm');
  protected readonly progressPercent = computed(() =>
    this.progressTotal() === 0 ? 0 : (100 * this.progressDone()) / this.progressTotal(),
  );

  /** The template's view of one row — the pure model, not re-implemented here. */
  protected readonly state = rowState;
  protected readonly appliable = isAppliable;
  protected readonly edited = isUserEdited;
  protected readonly value = effectiveValue;

  private program: string | null = null;
  private runSubscription: Subscription | null = null;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private closed = false;

  constructor() {
    this.feed.acquire();
  }

  ngOnDestroy(): void {
    this.closed = true;
    this.stopWatching();
    this.feed.release();
    // The host keeps the plan until it expires; say we are done with it so it goes now.
    if (this.program) void this.api.discard(this.data.folder, this.program).catch(() => undefined);
  }

  protected valueOf(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  protected preview(value: string | null): string {
    if (!value) return '';
    return value.length <= PREVIEW_CHARS ? value : `${value.slice(0, PREVIEW_CHARS)}…`;
  }

  protected isBehind(step: EditPhase): boolean {
    return PHASES.findIndex((p) => p.id === step) < PHASES.findIndex((p) => p.id === this.phase());
  }

  // ---- instruct ---------------------------------------------------------------------------------

  protected async analyze(): Promise<void> {
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

  protected async generate(): Promise<void> {
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
      this.stopWatching();
      this.error.set(toApiError(e).message);
      this.phase.set('plan');
    }
  }

  protected async cancelProposing(): Promise<void> {
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
    this.runSubscription = this.live.on('bookEdit').subscribe((message) => {
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
          this.stopWatching();
          this.error.set(message.reason ?? 'The AI could not compute the proposals.');
          this.phase.set('plan');
          break;
      }
    });
  }

  private poll(program: string): void {
    const pushing = this.runSubscription !== null;
    this.pollTimer = setTimeout(() => {
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
            this.error.set(run.reason ?? 'The AI could not compute the proposals.');
            this.phase.set('plan');
            return;
          }
        } catch (e) {
          // While the hub is pushing this is only the backstop: one failed read is not the run
          // failing, so keep watching. Without the hub it is the only signal, so say so.
          if (!pushing) {
            this.error.set(toApiError(e).message);
            this.phase.set('plan');
            return;
          }
        }
        this.poll(program);
      })();
    }, pushing ? PUSH_BACKSTOP_MS : POLL_MS);
  }

  private finishRun(rows: readonly BookEditRow[]): void {
    this.stopWatching();
    // New rows, so a new selection: every appliable row starts ticked.
    this.selection.set(ReviewSelection.from(rows));
    this.openRowId.set(null);
    this.phase.set('review');
  }

  private stopWatching(): void {
    this.runSubscription?.unsubscribe();
    this.runSubscription = null;
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  // ---- review -----------------------------------------------------------------------------------

  protected select(row: ReviewRow, selected: boolean): void {
    this.selection.update((s) => s.set(row, selected));
  }

  protected selectAll(): void {
    this.selection.update((s) => s.selectAll());
  }

  protected selectNone(): void {
    this.selection.update((s) => s.clear());
  }

  protected setValue(row: ReviewRow, value: string): void {
    this.selection.update((s) => s.setValue(row, value));
  }

  protected revert(row: ReviewRow): void {
    this.selection.update((s) => s.revert(row));
  }

  protected toggleOpen(row: ReviewRow): void {
    this.openRowId.update((open) => (open === row.proposal.id ? null : row.proposal.id));
  }

  /**
   * Re-asks the AI for this one row, steered by the sticky hint. Busy state is row-local so the
   * rest of the table stays usable, and a failure lands on the row rather than the dialog banner.
   */
  protected async retry(row: ReviewRow): Promise<void> {
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
  protected async apply(): Promise<void> {
    const edits = this.selection().toEdits();
    if (edits.length === 0) return;

    this.busy.set(true);
    try {
      const failure = await this.editor.tryExecute({ type: 'ApplyBookEdits', edits });
      if (failure) {
        this.error.set(`Applying edits failed: ${failure.message}`);
        return;
      }
      this.ref.close({ applied: edits.length });
    } finally {
      this.busy.set(false);
    }
  }

  protected async startOver(): Promise<void> {
    if (!(await this.confirmDiscard('Start over'))) return;
    await this.dropSession();
    this.backToInstruct();
  }

  protected async closeReview(): Promise<void> {
    if (!(await this.confirmDiscard('Close'))) return;
    this.close();
  }

  protected backToInstruct(): void {
    this.phase.set('instruct');
    this.error.set(null);
    this.selection.set(ReviewSelection.empty());
    this.openRowId.set(null);
  }

  protected close(): void {
    this.ref.close();
  }

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

/** Opens the full-screen Edit with AI dialog; resolves what it applied, or null when it was closed. */
export async function openEditWithAiDialog(
  dialog: MatDialog,
  data: EditWithAiDialogData,
  injector: Injector,
): Promise<EditWithAiDialogResult | null> {
  const ref = dialog.open<EditWithAiDialog, EditWithAiDialogData, EditWithAiDialogResult>(
    EditWithAiDialog,
    {
      data,
      panelClass: 'r2m-fullscreen-dialog',
      width: '100vw',
      maxWidth: '100vw',
      height: '100vh',
      autoFocus: 'dialog',
      restoreFocus: true,
      // The reader's injector, so the dialog applies through the same BookEditor the rows do.
      injector,
    },
  );
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
