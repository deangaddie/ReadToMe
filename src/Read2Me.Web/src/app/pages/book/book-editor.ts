import { Injectable, computed, inject, signal } from '@angular/core';
import {
  BookApi,
  BookCommand,
  CommandResponse,
  ManualImportRequest,
  ProjectsApi,
  toApiError,
} from '@app/api';
import { ToastService } from '@app/ui/toast/toast.service';
import { BookStore } from './book-store';

/** How long a committed write may take to come back as this tab's own receipt before we reload. */
export const OWN_RECEIPT_TIMEOUT_MS = 2000;

/**
 * The reader's one write path (ticket 11, design §8 "optimistic nothing"): posts a command or an
 * import, then waits for the hub to echo this tab's own receipt — which is what reloads the rows —
 * and only reloads by hand when no receipt arrives in time (a no-op commit, or a hub that is down).
 * A refusal (422) is shown verbatim as a toast; a success shows nothing, the change itself is the
 * feedback. `newEntityId` is ignored: the receipt-driven reload already covers what was created.
 */
@Injectable()
export class BookEditor {
  private readonly book = inject(BookApi);
  private readonly projects = inject(ProjectsApi);
  private readonly store = inject(BookStore);
  private readonly toast = inject(ToastService);

  private readonly _busy = signal(false);

  /** A write is in flight. */
  readonly busy = this._busy.asReadonly();
  /** Every mutation affordance is off: a write is in flight, or the view is stale (design §6.3). */
  readonly locked = computed(() => this._busy() || this.store.stale() !== null);

  /** Posts one command. Resolves true when the host accepted it. */
  async run(command: BookCommand): Promise<boolean> {
    return (await this.execute(command)) !== undefined;
  }

  /**
   * Posts one command and answers with the host's response — for a caller that needs the
   * `newEntityId` (creating a character to assign it). Undefined when refused or not sent.
   */
  async execute(command: BookCommand): Promise<CommandResponse | undefined> {
    return (await this.settle((folder) => this.book.execute(folder, command)))?.value;
  }

  /** Rereads the source file from scratch (the caller has already confirmed). */
  async reread(): Promise<boolean> {
    return (await this.settle((folder) => this.projects.import(folder, true))) !== null;
  }

  async rereadManually(request: ManualImportRequest): Promise<boolean> {
    return (
      (await this.settle((folder) => this.projects.importManually(folder, request))) !== null
    );
  }

  /** Runs one write; resolves its (possibly empty) result once the view has caught up, null when it failed. */
  private async settle<T>(write: (folder: string) => Promise<T>): Promise<{ value: T } | null> {
    const folder = this.store.folder();
    if (!folder || this._busy()) return null;

    // Armed before the request: the receipt can arrive before the HTTP response does.
    const receipt = this.store.expectOwnReceipt(OWN_RECEIPT_TIMEOUT_MS);
    this._busy.set(true);
    let value: T;
    try {
      value = await write(folder);
    } catch (error) {
      receipt.cancel();
      this._busy.set(false);
      this.toast.problem(toApiError(error).toProblem());
      return null;
    }

    try {
      if (!(await receipt.settled)) await this.store.refresh();
    } finally {
      this._busy.set(false);
    }
    return { value };
  }
}
