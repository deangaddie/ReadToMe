import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AiServicesApi, AssemblyApi, AttributionApi, AudioApi, VoicesApi } from '@app/api';
import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';
import { LiveSnapshot, StreamKind } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { ActivityDrawer } from './activity-drawer';
import { ActivityStore } from './activity-store';

/** Enough of LiveService for the store, the feeds and the services tab. */
class FakeLive {
  readonly queue = signal(null);
  readonly assembly = signal(IDLE_ASSEMBLY);
  readonly voiceBatch = signal(IDLE_VOICE_BATCH);
  readonly watchdog = signal({ llama: 'serviceHealthy' });
  readonly serviceStatus = signal({ llama: 'Ready' });
  readonly throughput = signal(null);
  readonly resynced$ = new Subject<LiveSnapshot>().asObservable();
  readonly joined: StreamKind[] = [];
  readonly left: StreamKind[] = [];

  on() {
    return new Subject<never>().asObservable();
  }
  async joinStream(kind: StreamKind) {
    this.joined.push(kind);
  }
  async leaveStream(kind: StreamKind) {
    this.left.push(kind);
  }
}

describe('ActivityDrawer', () => {
  function setup() {
    const live = new FakeLive();
    const noop = { cancel: vi.fn(), dismiss: vi.fn(), cancelBatch: vi.fn() };
    TestBed.configureTestingModule({
      imports: [ActivityDrawer],
      providers: [
        provideRouter([]),
        { provide: LiveService, useValue: live },
        { provide: ToastService, useValue: { success: vi.fn(), info: vi.fn(), error: vi.fn() } },
        { provide: AttributionApi, useValue: noop },
        { provide: AudioApi, useValue: noop },
        { provide: VoicesApi, useValue: noop },
        { provide: AssemblyApi, useValue: noop },
        {
          provide: AiServicesApi,
          useValue: {
            statusAll: async () => [],
            list: async () => [
              { name: 'llama', containerName: 'read2me-llama', baseUrl: '', usesGpu: true },
            ],
          },
        },
      ],
    });
    const store = TestBed.inject(ActivityStore);
    const fixture = TestBed.createComponent(ActivityDrawer);
    fixture.detectChanges();
    return { live, store, fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('opens on the Jobs tab with the empty state and no stream joined', () => {
    const { live, el } = setup();
    expect(el.querySelector('app-jobs-tab')).not.toBeNull();
    expect(el.textContent).toContain('No background work');
    expect(live.joined).toEqual([]);
  });

  it('joins a stream while its tab is visible and leaves when another tab is chosen', () => {
    const { live, store, fixture, el } = setup();

    store.tab.set('llm');
    fixture.detectChanges();
    expect(el.querySelector('app-llm-stream-tab')).not.toBeNull();
    expect(live.joined).toEqual(['llm']);

    store.tab.set('audio');
    fixture.detectChanges();
    expect(el.querySelector('app-llm-stream-tab')).toBeNull();
    expect(el.querySelector('app-audio-stream-tab')).not.toBeNull();
    expect(live.left).toEqual(['llm']);
    expect(live.joined).toEqual(['llm', 'audio']);
  });

  it('leaves the stream when the drawer is destroyed (closed)', () => {
    const { live, store, fixture } = setup();
    store.tab.set('llm');
    fixture.detectChanges();
    fixture.destroy();
    expect(live.left).toEqual(['llm']);
  });

  it('lists managed services with the hub-fed status and controls on the Services tab', async () => {
    const { store, fixture, el } = setup();
    store.tab.set('services');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.textContent).toContain('read2me-llama');
    expect(el.querySelector('r2m-docker-controls r2m-status-chip')?.textContent).toContain('Ready');
    expect(el.querySelector('r2m-docker-controls button[aria-label="Shutdown"]')).not.toBeNull();
    expect(el.querySelector('a[href="/settings/services"]')).not.toBeNull();
  });

  it('renders one tab link per section', () => {
    const { el } = setup();
    const labels = Array.from(el.querySelectorAll('.activity-drawer__tab-label')).map((a) =>
      a.textContent?.trim(),
    );
    expect(labels).toEqual(['Jobs', 'LLM', 'Audio', 'Services']);
  });
});
