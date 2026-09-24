import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { AiServiceDto, AiServicesApi, ApiError } from '@app/api';
import { LiveFamily, LiveMessageMap } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { AiServicesStore, WATCHDOG_LOG_LIMIT, watchdogLogEntry } from './ai-services-store';

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
  readonly subjects = new Map<LiveFamily, Subject<unknown>>();
  on<K extends LiveFamily>(family: K) {
    let s = this.subjects.get(family);
    if (!s) this.subjects.set(family, (s = new Subject()));
    return s.asObservable();
  }
  emit<K extends LiveFamily>(family: K, message: LiveMessageMap[K]) {
    this.subjects.get(family)?.next(message);
  }
}

describe('AiServicesStore', () => {
  let live: FakeLive;
  let api: {
    list: ReturnType<typeof vi.fn>;
    statusAll: ReturnType<typeof vi.fn>;
    status: ReturnType<typeof vi.fn>;
    start: ReturnType<typeof vi.fn>;
    restart: ReturnType<typeof vi.fn>;
    shutdown: ReturnType<typeof vi.fn>;
  };
  let toast: {
    success: ReturnType<typeof vi.fn>;
    error: ReturnType<typeof vi.fn>;
    warn: ReturnType<typeof vi.fn>;
    problem: ReturnType<typeof vi.fn>;
  };

  function store(): AiServicesStore {
    return TestBed.inject(AiServicesStore);
  }

  beforeEach(() => {
    live = new FakeLive();
    api = {
      list: vi.fn().mockResolvedValue(CATALOG),
      statusAll: vi.fn().mockResolvedValue([]),
      status: vi.fn().mockResolvedValue({ name: 'llama', status: 'Ready' }),
      start: vi.fn().mockResolvedValue(undefined),
      restart: vi.fn().mockResolvedValue(undefined),
      shutdown: vi.fn().mockResolvedValue(undefined),
    };
    toast = { success: vi.fn(), error: vi.fn(), warn: vi.fn(), problem: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: LiveService, useValue: live },
        { provide: AiServicesApi, useValue: api },
        { provide: ToastService, useValue: toast },
      ],
    });
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
