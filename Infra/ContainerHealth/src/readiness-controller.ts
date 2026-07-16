import type { ReadinessObservation, ServiceAdapter, ServiceId } from "./readiness";

export interface ControllerSnapshot {
  readonly adapter: ServiceAdapter;
  readonly checking: boolean;
  readonly observation?: ReadinessObservation;
}

export type RefreshResult = "committed" | "skipped" | "stale";
type Checker = (adapter: ServiceAdapter) => Promise<ReadinessObservation>;
type Listener = (snapshot: ControllerSnapshot, previous?: ReadinessObservation) => void;

interface MutableServiceState {
  adapter: ServiceAdapter;
  checking: boolean;
  epoch: number;
  observation?: ReadinessObservation;
}

export class ReadinessController {
  private readonly states = new Map<ServiceId, MutableServiceState>();

  constructor(
    adapters: readonly ServiceAdapter[],
    private readonly checker: Checker,
    private readonly listener: Listener = () => undefined
  ) {
    for (const adapter of adapters) {
      this.states.set(adapter.id, { adapter, checking: false, epoch: 0 });
    }
  }

  snapshots(): readonly ControllerSnapshot[] {
    return [...this.states.values()].map((state) => this.copy(state));
  }

  snapshot(id: ServiceId): ControllerSnapshot {
    const state = this.states.get(id);
    if (state === undefined) throw new Error(`Unknown service: ${id}`);
    return this.copy(state);
  }

  refreshAll(): Promise<void> {
    return Promise.all([...this.states.keys()].map((id) => this.refreshService(id))).then(() => undefined);
  }

  async refreshService(id: ServiceId): Promise<RefreshResult> {
    const service = this.states.get(id);
    if (service === undefined) throw new Error(`Unknown service: ${id}`);
    if (service.checking) return "skipped";

    service.checking = true;
    const epoch = ++service.epoch;
    this.listener(this.copy(service), service.observation);
    const observation = await this.checker(service.adapter);

    if (service.epoch !== epoch) return "stale";
    const previous = service.observation;
    service.observation = observation;
    service.checking = false;
    this.listener(this.copy(service), previous);
    return "committed";
  }

  invalidate(): void {
    for (const service of this.states.values()) {
      service.epoch += 1;
      if (service.checking) {
        service.checking = false;
        this.listener(this.copy(service), service.observation);
      }
    }
  }

  private copy(state: MutableServiceState): ControllerSnapshot {
    return state.observation === undefined
      ? { adapter: state.adapter, checking: state.checking }
      : { adapter: state.adapter, checking: state.checking, observation: state.observation };
  }
}

export type RefreshSeconds = 2 | 10 | 30;

export interface PollingClock {
  setTimeout(callback: () => void, delay: number): unknown;
  clearTimeout(handle: unknown): void;
}

const browserPollingClock: PollingClock = {
  setTimeout: (callback, delay) => globalThis.setTimeout(callback, delay),
  clearTimeout: (handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>)
};

export class ReadinessPolling {
  private visible = false;
  private started = false;
  private intervalSeconds: RefreshSeconds = 10;
  private timer: unknown;

  constructor(
    private readonly refresh: () => void,
    private readonly clock: PollingClock = browserPollingClock
  ) {}

  start(visible: boolean): void {
    if (this.started) return;
    this.started = true;
    this.visible = visible;
    if (visible) this.refreshAndSchedule();
  }

  setIntervalSeconds(seconds: RefreshSeconds): void {
    this.intervalSeconds = seconds;
    this.cancelSchedule();
    if (this.started && this.visible) this.refreshAndSchedule();
  }

  setVisible(visible: boolean): void {
    if (!this.started || visible === this.visible) return;
    this.visible = visible;
    this.cancelSchedule();
    if (visible) this.refreshAndSchedule();
  }

  refreshNow(): void {
    if (!this.started || !this.visible) return;
    this.cancelSchedule();
    this.refreshAndSchedule();
  }

  stop(): void {
    this.started = false;
    this.visible = false;
    this.cancelSchedule();
  }

  private refreshAndSchedule(): void {
    this.refresh();
    this.timer = this.clock.setTimeout(() => {
      this.timer = undefined;
      if (this.started && this.visible) this.refreshAndSchedule();
    }, this.intervalSeconds * 1_000);
  }

  private cancelSchedule(): void {
    if (this.timer !== undefined) {
      this.clock.clearTimeout(this.timer);
      this.timer = undefined;
    }
  }
}
