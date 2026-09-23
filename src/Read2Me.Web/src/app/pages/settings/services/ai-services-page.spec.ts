import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { AiServiceDto, AiServicesApi } from '@app/api';
import { LiveFamily, LiveMessageMap } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { AiServicesPage } from './ai-services-page';

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

describe('AiServicesPage', () => {
  let live: FakeLive;
  let api: Record<
    'list' | 'statusAll' | 'status' | 'start' | 'restart' | 'shutdown',
    ReturnType<typeof vi.fn>
  >;

  async function mount() {
    await TestBed.configureTestingModule({
      imports: [AiServicesPage],
      providers: [
        { provide: LiveService, useValue: live },
        { provide: AiServicesApi, useValue: api },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), warn: vi.fn(), problem: vi.fn() },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(AiServicesPage);
    await fixture.whenStable();
    return fixture;
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
  });

  it('renders a card per service with the GPU chip and hub-fed status', async () => {
    live.serviceStatus.set({ llama: 'Ready', whisper: 'Stopped' });
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const cards = Array.from(el.querySelectorAll('.services-page__card'));
    expect(cards.map((c) => c.getAttribute('data-service'))).toEqual(['llama', 'whisper']);
    expect(
      cards[0]?.querySelector('.services-page__card-head r2m-status-chip')?.textContent,
    ).toContain('GPU');
    expect(cards[1]?.querySelector('.services-page__card-head r2m-status-chip')).toBeNull();
    expect(cards[0]?.querySelector('r2m-docker-controls r2m-status-chip')?.textContent).toContain(
      'Ready',
    );
    expect(cards[1]?.querySelector('r2m-docker-controls r2m-status-chip')?.textContent).toContain(
      'Stopped',
    );
    expect(cards[0]?.textContent).toContain('read2me-llama');
    expect(api.statusAll).toHaveBeenCalledTimes(1);
  });

  it('a status flip over the hub re-renders the chip, and the buttons drive the store', async () => {
    live.serviceStatus.set({ llama: 'Ready' });
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const llama = () => el.querySelector('[data-service="llama"]')!;

    (llama().querySelector('button[aria-label="Shutdown"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(api.shutdown).toHaveBeenCalledWith('llama');
    expect(llama().querySelectorAll('button:disabled')).toHaveLength(4);

    live.serviceStatus.set({ llama: 'Stopped' });
    live.emit('serviceStatus', { name: 'llama', status: 'Stopped', op: 'shutdown', ok: true });
    await fixture.whenStable();
    expect(llama().querySelector('r2m-docker-controls r2m-status-chip')?.textContent).toContain(
      'Stopped',
    );
    expect(
      (llama().querySelector('button[aria-label="Start"]') as HTMLButtonElement).disabled,
    ).toBe(false);
  });

  it('lists watchdog events newest first with their labels', async () => {
    const fixture = await mount();
    live.emit('watchdog', {
      kind: 'recoveryStarted',
      service: 'llama',
      reason: 'health timed out',
    });
    live.emit('watchdog', { kind: 'serviceHealthy', service: 'llama' });
    await fixture.whenStable();
    const rows = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('.services-page__log-row'),
    );
    expect(rows.map((r) => r.getAttribute('data-kind'))).toEqual([
      'serviceHealthy',
      'recoveryStarted',
    ]);
    expect(rows[1]?.textContent).toContain('Recovery started');
    expect(rows[1]?.textContent).toContain('health timed out');
  });
});
