import { beforeEach, describe, expect, it, mock } from 'bun:test';
import { type AiServiceDto, AiServicesApi, ApiError } from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices, use } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { LiveFamily, LiveMessageMap } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { AiServicesStore, WATCHDOG_LOG_LIMIT, watchdogLogEntry } from './ai-services-store';

/** Ported from the Angular TestBed spec, case for case. */
const CATALOG: AiServiceDto[] = [
  {
    name: 'llama',
    containerName: 'read2me-llama',
    baseUrl: 'http://localhost:8080',
    usesGpu: true,
  },
  {
    name: 'whisper',
    containerName: 'read2me-whisper',
    baseUrl: 'http://localhost:9000',
    usesGpu: false,
  },
];

class FakeLive {
  readonly serviceStatus = signal<Record<string, 'Ready' | 'Stopped' | 'Unknown'>>({});
  readonly subjects = new Map<LiveFamily, Emitter<unknown>>();
  on<K extends LiveFamily>(family: K, listener: (m: LiveMessageMap[K]) => void) {
    let s = this.subjects.get(family);
    if (!s) this.subjects.set(family, (s = new Emitter()));
    return s.subscribe(listener as (m: unknown) => void);
  }
  emit<K extends LiveFamily>(family: K, message: LiveMessageMap[K]) {
    this.subjects.get(family)?.emit(message);
  }
}

describe('AiServicesStore', () => {
  let live: FakeLive;
  let api: {
    list: ReturnType<typeof mock>;
    statusAll: ReturnType<typeof mock>;
    status: ReturnType<typeof mock>;
    start: ReturnType<typeof mock>;
    restart: ReturnType<typeof mock>;
    shutdown: ReturnType<typeof mock>;
  };
  let toast: {
    success: ReturnType<typeof mock>;
    error: ReturnType<typeof mock>;
    warn: ReturnType<typeof mock>;
    problem: ReturnType<typeof mock>;
  };

  const store = (): AiServicesStore => use(AiServicesStore);

  beforeEach(() => {
    resetServices();
    live = new FakeLive();
    api = {
      list: mock(async () => CATALOG),
      statusAll: mock(async () => []),
      status: mock(async () => ({ name: 'llama', status: 'Ready' })),
      start: mock(async () => undefined),
      restart: mock(async () => undefined),
      shutdown: mock(async () => undefined),
    };
    toast = { success: mock(), error: mock(), warn: mock(), problem: mock() };
    override(LiveService, live as unknown as LiveService);
    override(AiServicesApi, api as unknown as AiServicesApi);
    override(ToastService, toast as unknown as ToastService);
  });

  it('loads the catalog once and probes everything through the one status call', async () => {
    const s = store();
    await Promise.all([s.ensureLoaded(), s.ensureLoaded()]);
    expect(api.list).toHaveBeenCalledTimes(1);
    expect(api.statusAll).toHaveBeenCalledTimes(1);
    expect(s.services()?.map((x) => x.name)).toEqual(['llama', 'whisper']);
    expect(s.gpuServices().map((x) => x.name)).toEqual(['llama']);
  });

  it('reads statuses from the live map, Unknown until observed', () => {
    const s = store();
    expect(s.statusOf('llama')).toBe('Unknown');
    live.serviceStatus.set({ llama: 'Ready' });
    expect(s.statusOf('llama')).toBe('Ready');
    expect(s.statusOf('LLAMA')).toBe('Ready');
  });

  it('keeps a service busy from the 202 until the hub reports the op, then toasts', async () => {
    const s = store();
    await s.shutdown('llama');
    expect(api.shutdown).toHaveBeenCalledWith('llama');
    expect(s.busy()).toEqual({ llama: 'shutdown' });

    // An outcome for another op (a probe, or someone else's start) is not this tab's answer.
    live.emit('serviceStatus', { name: 'llama', status: 'Ready' });
    expect(s.isBusy('llama')).toBe(true);

    live.emit('serviceStatus', { name: 'llama', status: 'Stopped', op: 'shutdown', ok: true });
    expect(s.busy()).toEqual({});
    expect(toast.success).toHaveBeenCalledWith('llama shut down');

    await s.start('llama');
    live.emit('serviceStatus', {
      name: 'llama',
      status: 'Down',
      op: 'start',
      ok: false,
      error: 'boom',
    });
    expect(toast.error).toHaveBeenCalledWith('Failed to start llama: boom');
    expect(s.isBusy('llama')).toBe(false);
  });

  it('ignores a second op while one is in flight and warns on the host saying so (409)', async () => {
    const s = store();
    await s.restart('llama');
    await s.shutdown('llama');
    expect(api.shutdown).not.toHaveBeenCalled();

    api.start.mockRejectedValue(
      new ApiError(409, 'Conflict', 'llama already has a restart in progress.'),
    );
    await s.start('whisper');
    expect(s.isBusy('whisper')).toBe(false);
    expect(toast.warn).toHaveBeenCalledWith('llama already has a restart in progress.');
  });

  it('refresh probes one service and is busy only for the request', async () => {
    const s = store();
    const pending = s.refresh('whisper');
    expect(s.busy()).toEqual({ whisper: 'refresh' });
    await pending;
    expect(api.status).toHaveBeenCalledWith('whisper');
    expect(s.busy()).toEqual({});
  });

  it('logs watchdog events newest first, capped', () => {
    const s = store();
    live.emit('watchdog', {
      kind: 'recoveryStarted',
      service: 'llama',
      reason: 'health timed out',
    });
    live.emit('watchdog', { kind: 'serviceHealthy', service: 'llama' });
    expect(s.log().map((e) => `${e.service}:${e.kind}`)).toEqual([
      'llama:serviceHealthy',
      'llama:recoveryStarted',
    ]);
    expect(s.log()[1]?.reason).toBe('health timed out');
    expect(s.log()[1]?.label).toBe('Recovery started');

    for (let i = 0; i < WATCHDOG_LOG_LIMIT + 5; i++) {
      live.emit('watchdog', { kind: 'containerRestarted', service: `s${i}` });
    }
    expect(s.log()).toHaveLength(WATCHDOG_LOG_LIMIT);
    expect(s.log()[0]?.service).toBe(`s${WATCHDOG_LOG_LIMIT + 4}`);
  });

  it('labels every watchdog kind', () => {
    expect(watchdogLogEntry({ kind: 'serviceDown', service: 'x', reason: 'gave up' }).label).toBe(
      'Down — recovery gave up',
    );
    expect(watchdogLogEntry({ kind: 'containerRestarted', service: 'x' }).label).toBe(
      'Container restarted',
    );
  });
});
