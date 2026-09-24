import { Injectable, computed, inject, signal } from '@angular/core';
import { AiServiceDto, AiServiceStatus, AiServicesApi, toApiError } from '@app/api';
import { ServiceOp, WatchdogKind, WatchdogMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';

/** What this tab is waiting on for a service: a lifecycle op it started, or a probe. */
export type ServiceBusy = ServiceOp | 'refresh';

/** One watchdog event as the services page logs it (newest first). */
export interface WatchdogLogEntry {
  at: Date;
  service: string;
  kind: WatchdogKind;
  label: string;
  reason?: string | null;
}

export const WATCHDOG_LOG_LIMIT = 100;

const WATCHDOG_LABEL: Record<WatchdogKind, string> = {
  recoveryStarted: 'Recovery started',
  containerRestarted: 'Container restarted',
  serviceHealthy: 'Healthy again',
  serviceDown: 'Down — recovery gave up',
};

const OP_PAST: Record<ServiceOp, string> = {
  start: 'started',
  restart: 'restarted',
  shutdown: 'shut down',
};

export function watchdogLogEntry(m: WatchdogMessage, at = new Date()): WatchdogLogEntry {
  return {
    at,
    service: m.service,
    kind: m.kind,
    label: WATCHDOG_LABEL[m.kind] ?? m.kind,
    reason: m.reason ?? null,
  };
}

/**
 * The managed AI services as one app-wide store (ticket 25): the catalog, each service's last
 * observed status (from `LiveService.serviceStatus`, which the hub keeps current), which services
 * this tab has an op in flight on, and the session's watchdog log. Start / Restart / Shutdown
 * answer 202; the outcome arrives as a `serviceStatus` message carrying the op, which is when the
 * busy state clears and the toast shows. Created at app start so the log misses nothing.
 */
@Injectable({ providedIn: 'root' })
export class AiServicesStore {
  private readonly api = inject(AiServicesApi);
  private readonly live = inject(LiveService);
  private readonly toast = inject(ToastService);

  private readonly _services = signal<AiServiceDto[] | null>(null);
  private readonly _error = signal<string | null>(null);
  private readonly _busy = signal<Record<string, ServiceBusy>>({});
  private readonly _log = signal<WatchdogLogEntry[]>([]);
  private readonly _refreshingAll = signal(false);
  private loading: Promise<void> | null = null;

  /** The catalog, or null before the first load. */
  readonly services = this._services.asReadonly();
  readonly error = this._error.asReadonly();
  /** Per service: the op this tab is waiting on. */
  readonly busy = this._busy.asReadonly();
  /** Watchdog events seen this session, newest first, at most {@link WATCHDOG_LOG_LIMIT}. */
  readonly log = this._log.asReadonly();
  readonly refreshingAll = this._refreshingAll.asReadonly();
  /** Last observed status per service name; `Unknown` until something observed it. */
  readonly statuses = this.live.serviceStatus;
  readonly gpuServices = computed(() => (this._services() ?? []).filter((s) => s.usesGpu));

  constructor() {
    this.live.on('watchdog').subscribe((m) => this.logWatchdog(m));
    this.live.on('serviceStatus').subscribe((m) => {
      if (!m.op) return;
      const waiting = this._busy()[m.name];
      if (waiting !== m.op) return;
      this.clearBusy(m.name);
      if (m.ok) this.toast.success(`${m.name} ${OP_PAST[m.op]}`);
      else this.toast.error(`Failed to ${m.op} ${m.name}: ${m.error ?? 'unknown error'}`);
    });
  }

  statusOf(name: string): AiServiceStatus {
    const statuses = this.statuses();
    const exact = statuses[name];
    if (exact) return exact;
    const key = name.toLowerCase();
    for (const [k, v] of Object.entries(statuses)) if (k.toLowerCase() === key) return v;
    return 'Unknown';
  }

  isBusy(name: string): boolean {
    return name in this._busy();
  }

  /** Loads the catalog and probes every service once; later calls return the same load. */
  ensureLoaded(): Promise<void> {
    return (this.loading ??= this.load());
  }

  private async load(): Promise<void> {
    try {
      this._services.set(await this.api.list());
      this._error.set(null);
      await this.refreshAll();
    } catch (error) {
      this._error.set(toApiError(error).message);
      this.loading = null;
    }
  }

  /** One probe per service, in one request; the hub delivers the statuses. */
  async refreshAll(): Promise<void> {
    this._refreshingAll.set(true);
    try {
      await this.api.statusAll();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this._refreshingAll.set(false);
    }
  }

  async refresh(name: string): Promise<void> {
    if (this.isBusy(name)) return;
    this.setBusy(name, 'refresh');
    try {
      await this.api.status(name);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.clearBusy(name);
    }
  }

  start(name: string): Promise<void> {
    return this.run(name, 'start', () => this.api.start(name));
  }

  restart(name: string): Promise<void> {
    return this.run(name, 'restart', () => this.api.restart(name));
  }

  shutdown(name: string): Promise<void> {
    return this.run(name, 'shutdown', () => this.api.shutdown(name));
  }

  private async run(name: string, op: ServiceOp, send: () => Promise<void>): Promise<void> {
    if (this.isBusy(name)) return;
    this.setBusy(name, op);
    try {
      await send();
      // Busy stays until the hub reports the outcome (a start can take minutes).
    } catch (error) {
      this.clearBusy(name);
      const problem = toApiError(error);
      if (problem.status === 409) this.toast.warn(problem.message);
      else this.toast.problem(problem.toProblem());
    }
  }

  private logWatchdog(m: WatchdogMessage): void {
    this._log.update((log) => [watchdogLogEntry(m), ...log].slice(0, WATCHDOG_LOG_LIMIT));
  }

  private setBusy(name: string, what: ServiceBusy): void {
    this._busy.update((b) => ({ ...b, [name]: what }));
  }

  private clearBusy(name: string): void {
    this._busy.update((b) => {
      if (!(name in b)) return b;
      const next = { ...b };
      delete next[name];
      return next;
    });
  }
}
