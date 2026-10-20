import { type Guid, HistoryApi, type HistoryStep } from '@app/api';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { HistoryMessage, LiveHistoryStep } from '@app/live/live-messages';
import { LiveService, type Unsubscribe } from '@app/live/live.service';

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
 */
export class HistoryStore {
  private readonly api = use(HistoryApi);
  private readonly live = use(LiveService);

  private subscriptions: Unsubscribe[] = [];
  /** Hub messages that arrived while the list was being read, applied over it once it lands. */
  private held: HistoryMessage[] | null = null;
  private reading: Promise<void> = Promise.resolve();

  private readonly _folder = signal<string | null>(null);
  private readonly _steps = signal<HistoryStep[]>([]);
  private readonly _headStepId = signal<Guid | null>(null);
  private readonly _loaded = signal(false);
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);

  readonly folder = this._folder.asReadonly();
  /** Newest first. */
  readonly steps = this._steps.asReadonly();
  /** The tip, which the sheet marks "Current"; null before the first step. */
  readonly headStepId = this._headStepId.asReadonly();
  readonly loaded = this._loaded.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();

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
    this._folder.set(null);
    this._steps.set([]);
    this._headStepId.set(null);
    this._loaded.set(false);
    this._error.set(null);
    this.held = null;
  }

  /** Reads the list the first time the sheet asks; after that the hub keeps it current. */
  load(): Promise<void> {
    if (!this._loaded() && !this._loading()) this.reading = this.read();
    return this.reading;
  }

  /** Resolves once the read in flight, if any, has landed. */
  settled(): Promise<void> {
    return this.reading;
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
    else if (this._loaded()) this.apply(message, { overList: false });
  }

  /**
   * Replaces the row with the message's `stepId`, or puts a new one on top as the Head: steps only
   * ever land at the tip. A message held while the list was read may be older than the list, so it
   * never reopens a row the list has closed.
   */
  private apply(message: HistoryMessage, { overList }: { overList: boolean }): void {
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
}

/** The hub omits null fields; the list and the sheet read them as null. */
function fromHub(step: LiveHistoryStep): HistoryStep {
  return {
    ...step,
    parentStepId: step.parentStepId ?? null,
    summary: step.summary ?? null,
    endedAt: step.endedAt ?? null,
    note: step.note ?? null,
    restoredFromStepId: step.restoredFromStepId ?? null,
  };
}
