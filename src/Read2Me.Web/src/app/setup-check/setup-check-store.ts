import { type SetupCheckReport, SetupCheckApi, toApiError } from '@app/api';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { ToastService } from '@app/ui/toast';

/**
 * The Setup check report as one app-wide store: the only source the Setup check page reads (and,
 * later, the start banner and the app-bar shield). It holds the host's last report and whether
 * this tab has a Quick check in flight.
 */
export class SetupCheckStore {
  private readonly api = use(SetupCheckApi);
  private readonly toast = use(ToastService);

  private readonly _report = signal<SetupCheckReport | null>(null);
  private readonly _error = signal<string | null>(null);
  private readonly _checking = signal(false);

  /** The last report, or null before the first read. */
  readonly report = this._report.asReadonly();
  /** Why the first read failed, while nothing has loaded. */
  readonly error = this._error.asReadonly();
  /** A Quick check this tab asked for is running. */
  readonly checking = this._checking.asReadonly();

  /** What opening the page does: show the host's report at once, then run a fresh Quick check. */
  async open(): Promise<void> {
    try {
      this._report.set(await this.api.report());
      this._error.set(null);
    } catch (error) {
      if (this._report() === null) this._error.set(toApiError(error).message);
    }
    await this.runQuick();
  }

  async runQuick(): Promise<void> {
    if (this._checking()) return;
    this._checking.set(true);
    try {
      this._report.set(await this.api.runQuick());
      this._error.set(null);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this._checking.set(false);
    }
  }
}
