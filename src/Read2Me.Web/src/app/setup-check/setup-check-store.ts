import { type SetupCheckReport, SetupCheckApi, toApiError } from '@app/api';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { ToastService } from '@app/ui/toast';
import { bannerVisible, failedCount, shieldView } from './surfacing';

const DISMISSED_KEY = 'r2m.setupCheck.bannerDismissedStart';
/**
 * How often, and how many times, {@link SetupCheckStore.load} reads again while the start-up check
 * runs. A stand-in until the Setup check hub family (#78) streams the report; the cap outlasts a
 * Quick check whose every process and probe times out.
 */
const START_POLL_MS = 1000;
const START_POLL_TRIES = 180;

/**
 * The Setup check report as one app-wide store: the only source the Setup check page, the start
 * banner and the app-bar shield read. It holds the host's last report, whether this tab has a
 * Quick check in flight, and the host start the banner was dismissed on.
 */
export class SetupCheckStore {
  private readonly api = use(SetupCheckApi);
  private readonly toast = use(ToastService);

  private readonly _report = signal<SetupCheckReport | null>(null);
  private readonly _error = signal<string | null>(null);
  private readonly _checking = signal(false);
  private readonly _dismissedStart = signal(readDismissed());

  /** The last report, or null before the first read. */
  readonly report = this._report.asReadonly();
  /** Why the first read failed, while nothing has loaded. */
  readonly error = this._error.asReadonly();
  /** A Quick check this tab asked for is running. */
  readonly checking = this._checking.asReadonly();
  /** The start banner: something failed, and it wasn't dismissed on this host start. */
  readonly bannerVisible = computed(() => bannerVisible(this._report(), this._dismissedStart()));
  /** How many checks in the report failed. */
  readonly failedCount = computed(() => failedCount(this._report()));
  /** The app-bar shield's state. */
  readonly shield = computed(() => shieldView(this._report()));

  /**
   * What the app does as it starts: reads the report, so the first-run flag is known before the
   * router lands, then keeps reading in the background until the start-up Quick check has run.
   * An unreachable host leaves the report null (the page shows the error when opened).
   */
  async load(): Promise<void> {
    if (!(await this.read())) return;
    void this.awaitStartCheck();
  }

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

  /** Hides the start banner until the host starts again. */
  dismissBanner(): void {
    const start = this._report()?.startedAt;
    if (!start) return;
    this._dismissedStart.set(start);
    try {
      localStorage.setItem(DISMISSED_KEY, start);
    } catch {
      // storage unavailable — dismissed for this tab only
    }
  }

  private async read(): Promise<boolean> {
    try {
      this._report.set(await this.api.report());
      return true;
    } catch {
      return false;
    }
  }

  private async awaitStartCheck(): Promise<void> {
    for (let i = 0; i < START_POLL_TRIES && !this._report()?.quickCheckedAt; i++) {
      await new Promise((resolve) => setTimeout(resolve, START_POLL_MS));
      // A Quick check run from the page in the meantime answers for itself.
      if (this._report()?.quickCheckedAt) return;
      if (!(await this.read())) return;
    }
  }
}

function readDismissed(): string | null {
  try {
    return localStorage.getItem(DISMISSED_KEY);
  } catch {
    return null;
  }
}
