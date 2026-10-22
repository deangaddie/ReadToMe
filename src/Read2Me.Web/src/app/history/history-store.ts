import { type Guid, HistoryApi, type HistoryStep, StepView, toApiError } from '@app/api';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import type { HistoryMessage, LiveHistoryStep } from '@app/live/live-messages';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';

/** What the viewing banner says when the viewed step didn't open: for good, or just now. */
const CANT_OPEN = "This step can't be opened.";
const TRY_AGAIN = "Couldn't open this step just now. Try again.";

/**
 * One project's **History** (context/history.md) for the History sheet: the step list newest first
 * and the Head, read once when the sheet first asks, then kept live from the hub's `history`
 * family, whichever tab or agent made the change. The project shell opens it with the folder and
 * provides it; the list is not read until {@link load}, so a project whose sheet is never opened
 * costs no request.
 * <p>
 * Rows are replaced by `stepId`, never appended blindly: a step lands open and is sent again when
 * it closes, and a closed step is sent again when its note changes ("part 1" is written once part 2
 * starts).
 * <p>
 * It also drives **View a step**: the step the whole web app shows the project at ({@link viewing},
 * a step id so it follows the row as the hub replaces it), kept in the app-wide {@link StepView}
 * so every Project state read carries `?at=`. A busy project can't be viewed
 * ({@link busy}, from the list and the hub's `busyChanged`); background work starting while a tab
 * views a step sends it back to current with a toast.
 * <p>
 * And **Restore** (spec, "Restore"): viewing a step reads what restoring it would cost
 * ({@link restorePreview}, for the banner to say before the click); {@link restore} makes the
 * viewed step current as a new step, returns the tab to current and toasts Show in Book and Undo
 * restore (a Restore to the tip before). A Restore another tab or an agent made is toasted when
 * the hub says it finished; the page itself rebuilds from the Restore's receipt as it does for any
 * other write.
 */
export class HistoryStore {
  private readonly api = use(HistoryApi);
  private readonly live = use(LiveService);
  private readonly stepView = use(StepView);
  private readonly toast = use(ToastService);

  private subscriptions: Unsubscribe[] = [];
  /** Hub messages that arrived while the list was being read, applied over it once it lands. */
  private held: HistoryMessage[] | null = null;
  private reading: Promise<void> = Promise.resolve();
  /** The preview reads and Restores in flight, for {@link settled}. */
  private pending: Promise<void> = Promise.resolve();
  /** This tab's own Restore is in flight: its finish on the hub is not "another tab". */
  private restoringHere = false;
  /** The Restore steps this tab added, whose finish may reach the hub after the answer did. */
  private readonly ownRestoreSteps = new Set<Guid>();

  private readonly _folder = signal<string | null>(null);
  private readonly _steps = signal<HistoryStep[]>([]);
  private readonly _headStepId = signal<Guid | null>(null);
  private readonly _loaded = signal(false);
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);
  private readonly _busy = signal<string | null>(null);
  private readonly _restoring = signal(false);
  private readonly _restorePreview = signal<number | null>(null);
  private readonly _viewError = signal<string | null>(null);

  readonly folder = this._folder.asReadonly();
  /** Newest first. */
  readonly steps = this._steps.asReadonly();
  /** The tip, which the sheet marks "Current"; null before the first step. */
  readonly headStepId = this._headStepId.asReadonly();
  readonly loaded = this._loaded.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();
  /** The step the project is viewed at, or null for the current project. */
  readonly viewing = computed(() => {
    const folder = this._folder();
    return folder === null ? null : this.stepView.at(folder);
  });
  /** Why the project is busy ("Attribution: 128 paragraphs left"), or null when idle. */
  readonly busy = this._busy.asReadonly();
  /** A Restore of the project is running, here or elsewhere; every other write is refused meanwhile. */
  readonly restoring = this._restoring.asReadonly();
  /** How many items would lose their audio if the viewed step were restored now; null until read. */
  readonly restorePreview = this._restorePreview.asReadonly();
  /**
   * Why the viewed step didn't open, for the banner: it can't be opened (marked for good), or it
   * couldn't just now (503), when viewing it again may work. Null while it opens or opened.
   */
  readonly viewError = this._viewError.asReadonly();
  /** The viewed step's row, as the hub last sent it. */
  readonly viewedStep = computed(() => {
    const id = this.viewing();
    return id === null ? null : (this._steps().find((s) => s.stepId === id) ?? null);
  });

  /** Follows `folder`'s History on the hub; the project shell has joined its group. Reads nothing yet. */
  open(folder: string): void {
    this.close();
    this._folder.set(folder);
    this.subscriptions = [
      this.live.on('history', (m) => {
        if (m.folder === this._folder()) this.receive(m);
      }),
      // Steps may have landed while the hub was away: reread, but only a list someone has asked for.
      this.live.resynced(() => {
        if (this._loaded()) this.reading = this.read();
      }),
    ];
  }

  close(): void {
    for (const unsubscribe of this.subscriptions) unsubscribe();
    this.subscriptions = [];
    this.backToCurrent();
    this._folder.set(null);
    this._steps.set([]);
    this._headStepId.set(null);
    this._loaded.set(false);
    this._error.set(null);
    this._busy.set(null);
    this._restoring.set(false);
    this.held = null;
  }

  /** Reads the list the first time the sheet asks; after that the hub keeps it current. */
  load(): Promise<void> {
    if (!this._loaded() && !this._loading()) this.reading = this.read();
    return this.reading;
  }

  /** Resolves once the reads and writes in flight, if any, have landed. */
  settled(): Promise<void> {
    return Promise.all([this.reading, this.pending]).then(() => undefined);
  }

  /** Follows a preview read or a Restore, so {@link settled} covers it; the list read keeps its own handle. */
  private track(work: Promise<void>): void {
    this.pending = Promise.all([this.pending, work]).then(() => undefined);
  }

  /**
   * Shows the project as it stood after `stepId`; the tip is the current project. False, and
   * nothing changes, while the project is busy.
   */
  view(stepId: Guid): boolean {
    const folder = this._folder();
    if (folder === null || this._busy() !== null) return false;
    if (stepId === this._headStepId()) {
      this.backToCurrent();
      return true;
    }
    this.stepView.view(folder, stepId);
    this._restorePreview.set(null);
    this._viewError.set(null);
    this.track(this.readPreview(folder, stepId));
    return true;
  }

  backToCurrent(): void {
    const folder = this._folder();
    if (folder !== null) this.stepView.clear(folder);
    this._restorePreview.set(null);
    this._viewError.set(null);
  }

  /**
   * Restores `stepId` (the viewed step by default): the project as it stood after it becomes
   * current, as a new step, and the later steps stay. The tab returns to current, and the toast
   * offers Show in Book and Undo restore. A refusal (busy, already restoring) is toasted and the
   * tab stays where it was.
   */
  async restore(stepId: Guid | null = this.viewing()): Promise<void> {
    const folder = this._folder();
    if (folder === null || stepId === null) return;
    const previousTip = this._headStepId();
    this.restoringHere = true;
    try {
      const result = await this.api.restore(folder, stepId);
      if (this._folder() !== folder) return;
      this.backToCurrent();
      const name = this.stepName(stepId);
      if (result.step === null) {
        this.toast.info(`The project already is as it was after ${name}.`);
        return;
      }
      this.ownRestoreSteps.add(result.step.stepId);
      const count = result.audioDiscardCount;
      this.toast.info(
        `Restored to ${name}. The later steps stay in History; ${count} item${count === 1 ? '' : 's'} need${count === 1 ? 's' : ''} generating.`,
        [
          {
            label: 'Show in Book',
            run: () => use(Router).navigate(`projects/${encodeURIComponent(folder)}/book`),
          },
          { label: 'Undo restore', run: () => this.track(this.restore(previousTip)) },
        ],
      );
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.restoringHere = false;
    }
  }

  /** "#8 Split chapter", or "a past step" for one the list doesn't hold. */
  private stepName(stepId: Guid): string {
    const step = this._steps().find((s) => s.stepId === stepId);
    return step ? stepName(step) : 'a past step';
  }

  private async readPreview(folder: string, stepId: Guid): Promise<void> {
    try {
      const step = await this.api.step(folder, stepId);
      if (this.viewing() !== stepId) return;
      this._restorePreview.set(step.restorePreview?.audioDiscardCount ?? null);
      if (step.cantOpen) this._viewError.set(CANT_OPEN);
    } catch (e) {
      // The banner keeps its Restore disabled: the cost can't be stated; a 503 says to try again.
      // A busy answer also arrives as busyChanged, which sends the tab back to current.
      if (this.viewing() === stepId && toApiError(e).status === 503) this._viewError.set(TRY_AGAIN);
    }
  }

  private async read(): Promise<void> {
    const folder = this._folder();
    if (folder === null) return;
    this.held = [];
    this._loading.set(true);
    try {
      const list = await this.api.list(folder);
      if (this._folder() !== folder) return;
      const held = this.held;
      this.held = null;
      this._steps.set(list.steps);
      this._headStepId.set(list.headStepId);
      this._restoring.set(list.restoring);
      this.setBusy(list.busy?.reason ?? null);
      for (const message of held) this.apply(message, { overList: true });
      this._loaded.set(true);
      this._error.set(null);
    } catch (e) {
      if (this._folder() === folder) this._error.set(e instanceof Error ? e.message : String(e));
    } finally {
      this.held = null;
      if (this._folder() === folder) this._loading.set(false);
    }
  }

  private receive(message: HistoryMessage): void {
    if (this.held) this.held.push(message);
    // The busy and restoring answers matter whether or not the list was ever read.
    else if (this._loaded() || message.kind === 'busyChanged' || message.kind === 'restoring')
      this.apply(message, { overList: false });
  }

  /**
   * Replaces the row with the message's `stepId`, or puts a new one on top as the Head: steps only
   * ever land at the tip. A message held while the list was read may be older than the list, so it
   * never reopens a row the list has closed.
   */
  private apply(message: HistoryMessage, { overList }: { overList: boolean }): void {
    if (message.kind === 'busyChanged') {
      this.setBusy(message.reason ?? null);
      return;
    }
    // The Restore step itself lands and closes like any other; the restoring phase is not a row.
    if (message.kind === 'restoring') {
      this.setRestoring(message.phase === 'started', message.step);
      return;
    }
    const incoming = fromHub(message.step);
    const steps = this._steps();
    const at = steps.findIndex((s) => s.stepId === incoming.stepId);
    if (at < 0) {
      this._steps.set([incoming, ...steps]);
      this._headStepId.set(incoming.stepId);
      return;
    }
    if (overList && incoming.isOpen && !steps[at]!.isOpen) return;
    this._steps.set(steps.with(at, incoming));
  }

  /**
   * A Restore finishing elsewhere (another tab, an agent) is said so, naming the step it brought
   * back (by the Restore step's own label when the list doesn't hold it); the page rebuilds from
   * the Restore's receipt. This tab's own Restore toasts from its answer instead, which carries the
   * audio count. A tab viewing a step when a Restore starts goes back to current: the step it
   * shows is about to stop being the past of the current project.
   */
  private setRestoring(restoring: boolean, step: LiveHistoryStep | undefined): void {
    this._restoring.set(restoring);
    if (restoring) {
      if (!this.restoringHere) this.backToCurrent();
      return;
    }
    if (!step || this.restoringHere || this.ownRestoreSteps.delete(step.stepId)) return;
    const from = step.restoredFromStepId;
    const fromStep = from ? this._steps().find((s) => s.stepId === from) : undefined;
    this.toast.info(`Project restored to ${stepName(fromStep ?? step)} in another tab`);
  }

  /** A viewing tab can't stay on a past step the work is racing past. */
  private setBusy(reason: string | null): void {
    this._busy.set(reason);
    if (reason === null || this.viewing() === null) return;
    this.backToCurrent();
    this.toast.info(`Back to the current project: background work started (${reason}).`);
  }
}

/** "#8 Split chapter": how toasts name a step. */
function stepName(step: Pick<HistoryStep, 'sequence' | 'label'>): string {
  return `#${step.sequence} ${step.label}`;
}

/** The hub omits null fields; the list and the sheet read them as null. */
function fromHub(step: LiveHistoryStep): HistoryStep {
  return {
    ...step,
    parentStepId: step.parentStepId ?? null,
    summary: step.summary ?? null,
    endedAt: step.endedAt ?? null,
    note: step.note ?? null,
    action: step.action ?? null,
    restoredFromStepId: step.restoredFromStepId ?? null,
  };
}
