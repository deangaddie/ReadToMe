import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { AssemblyApi, AttributionApi, AudioApi, VoicesApi, toApiError } from '@app/api';
import { QueueMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { JobView } from '@app/ui/job-pill/job';
import { ToastService } from '@app/ui/toast/toast.service';
import {
  ASSEMBLY_JOB_ID,
  ATTRIBUTION_JOB_ID,
  AUDIO_JOB_ID,
  VOICE_BATCH_JOB_ID,
  WATCHDOG_JOB_PREFIX,
  deriveJobs,
  isActiveJob,
  isQueueBusy,
} from './activity-jobs';

export type ActivityTab = 'jobs' | 'llm' | 'audio' | 'services';

/** Client-side elapsed/ETA tick while a queue is busy (Blazor's StatusDock used 1 s too). */
export const ELAPSED_TICK_MS = 1000;

interface LocalJob {
  job: JobView;
  cancel?: () => void | Promise<void>;
}

/**
 * The activity centre's state (ticket 14, design §5): job view models derived from the live hub's
 * snapshot-shaped signals, the drawer's open/tab state, cancel and dismiss, and toasts for jobs that
 * finish while the user is looking elsewhere. Elapsed and ETA tick client-side between queue pushes
 * from the moment the last one arrived. Feature code registers jobs the hub does not know about
 * (the single voice-prompt run) with {@link registerLocalJob}.
 */
@Injectable({ providedIn: 'root' })
export class ActivityStore implements OnDestroy {
  private readonly live = inject(LiveService);
  private readonly toast = inject(ToastService);
  private readonly attributionApi = inject(AttributionApi);
  private readonly audioApi = inject(AudioApi);
  private readonly voicesApi = inject(VoicesApi);
  private readonly assemblyApi = inject(AssemblyApi);

  private readonly subscriptions = new Subscription();
  private ticker: ReturnType<typeof setInterval> | null = null;

  private readonly _now = signal(Date.now());
  private readonly _queueReceivedAt = signal(Date.now());
  private readonly _dismissed = signal<ReadonlySet<string>>(new Set());
  private readonly _throughputDismissed = signal(false);
  private readonly _localJobs = signal<LocalJob[]>([]);

  readonly drawerOpen = signal(false);
  readonly tab = signal<ActivityTab>('jobs');

  private readonly sinceQueueSeconds = computed(() =>
    Math.max(0, (this._now() - this._queueReceivedAt()) / 1000),
  );

  /** Every job worth a card: active ones plus failed ones the user has not dismissed. */
  readonly jobs = computed(() =>
    deriveJobs({
      queue: this.live.queue(),
      assembly: this.live.assembly(),
      voiceBatch: this.live.voiceBatch(),
      watchdog: this.live.watchdog(),
      sinceQueueSeconds: this.sinceQueueSeconds(),
      localJobs: this._localJobs().map(({ job, cancel }) => ({ ...job, cancellable: !!cancel })),
      dismissed: this._dismissed(),
    }),
  );
  /** The activity bar's pills. */
  readonly activeJobs = computed(() => this.jobs().filter(isActiveJob));
  readonly hasActive = computed(() => this.activeJobs().length > 0);

  readonly throughput = this.live.throughput;
  /** `null` buckets (nothing measured) draw as zero. */
  readonly throughputHistory = computed(
    () => this.throughput()?.generationRateHistory.map((v) => v ?? 0) ?? [],
  );
  /** Headline + sparkline: there has been a run and the user has not dismissed it. */
  readonly showThroughput = computed(
    () => !!this.throughput()?.hasRun && !this._throughputDismissed(),
  );
  /** Per-config table only after the run ends (Blazor: `HasRun && !IsRunActive`). */
  readonly showThroughputTable = computed(
    () => this.showThroughput() && !this.throughput()!.isRunActive,
  );

  constructor() {
    if (this.live.queue()) this.stampQueue(this.live.queue());
    this.subscriptions.add(this.live.on('queue').subscribe((m) => this.stampQueue(m)));
    this.subscriptions.add(this.live.resynced$.subscribe(() => this.stampQueue(this.live.queue())));
    this.subscriptions.add(
      this.live.on('assembly').subscribe((m) => {
        if (m.kind === 'phaseStarted') this.undismiss(ASSEMBLY_JOB_ID);
        if (m.kind === 'completed') this.toast.success('Assembly finished');
        if (m.kind === 'failed') this.toast.error(`Assembly failed: ${firstLine(m.reason)}`);
        if (m.kind === 'cancelled') this.toast.info('Assembly cancelled');
      }),
    );
    this.subscriptions.add(
      this.live.on('voiceBatch').subscribe((m) => {
        if (m.kind === 'started') this.undismiss(VOICE_BATCH_JOB_ID);
        if (m.kind === 'completed') {
          this.toast.success(
            `Voice batch complete: ${m.processed ?? 0} done, ${m.failed ?? 0} failed`,
          );
        }
        if (m.kind === 'cancelled') this.toast.info('Voice batch cancelled');
      }),
    );
    this.subscriptions.add(
      this.live.on('watchdog').subscribe((m) => {
        if (m.kind === 'recoveryStarted') this.undismiss(`${WATCHDOG_JOB_PREFIX}${m.service}`);
        if (m.kind === 'serviceDown') this.toast.error(`${m.service} is down`);
      }),
    );
    this.subscriptions.add(
      this.live.on('throughput').subscribe((t) => {
        if (t.isRunActive) this._throughputDismissed.set(false);
      }),
    );
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
    this.stopTicker();
  }

  // ---- drawer ------------------------------------------------------------------------------------

  openDrawer(tab?: ActivityTab): void {
    if (tab) this.tab.set(tab);
    this.drawerOpen.set(true);
  }

  closeDrawer(): void {
    this.drawerOpen.set(false);
  }

  toggleDrawer(): void {
    this.drawerOpen.update((open) => !open);
  }

  // ---- actions -----------------------------------------------------------------------------------

  /** Cancel by job id: the four global queues each have an endpoint; local jobs bring their own. */
  async cancel(id: string): Promise<void> {
    try {
      switch (id) {
        case ATTRIBUTION_JOB_ID:
          await this.attributionApi.cancel();
          return;
        case AUDIO_JOB_ID:
          await this.audioApi.cancel();
          return;
        case VOICE_BATCH_JOB_ID:
          await this.voicesApi.cancelBatch();
          return;
        case ASSEMBLY_JOB_ID:
          await this.assemblyApi.cancel();
          return;
        default:
          await this._localJobs()
            .find((l) => l.job.id === id)
            ?.cancel?.();
      }
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    }
  }

  /** Hides a failed/finished job's card until that job starts again. */
  dismiss(id: string): void {
    this._dismissed.update((set) => new Set(set).add(id));
  }

  /** Blazor's Dismiss: retires the finished run's throughput block here and on the host. */
  async dismissThroughput(): Promise<void> {
    this._throughputDismissed.set(true);
    try {
      await this.attributionApi.dismiss();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    }
  }

  /**
   * Adds a job the hub does not report (the single voice-prompt run). Returns the unregister
   * function; `cancel`, when given, makes the pill cancellable.
   */
  registerLocalJob(job: JobView, cancel?: () => void | Promise<void>): () => void {
    const entry: LocalJob = cancel ? { job, cancel } : { job };
    this._localJobs.update((all) => [...all.filter((l) => l.job.id !== job.id), entry]);
    return () => this._localJobs.update((all) => all.filter((l) => l !== entry));
  }

  // ---- ticking -----------------------------------------------------------------------------------

  private stampQueue(queue: QueueMessage | null): void {
    const now = Date.now();
    this._now.set(now);
    this._queueReceivedAt.set(now);
    if (queue && isQueueBusy(queue)) this.startTicker();
    else this.stopTicker();
  }

  private startTicker(): void {
    if (this.ticker) return;
    this.ticker = setInterval(() => this._now.set(Date.now()), ELAPSED_TICK_MS);
  }

  private stopTicker(): void {
    if (!this.ticker) return;
    clearInterval(this.ticker);
    this.ticker = null;
  }

  private undismiss(id: string): void {
    if (!this._dismissed().has(id)) return;
    this._dismissed.update((set) => {
      const next = new Set(set);
      next.delete(id);
      return next;
    });
  }
}

/** Toasts get the first line of a reason; ffmpeg's full stderr belongs on the job card. */
function firstLine(reason: string | null | undefined): string {
  const line = (reason ?? '').split(/\r?\n/, 1)[0]?.trim();
  return line || 'unknown error';
}
