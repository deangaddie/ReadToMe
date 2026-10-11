import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { AiServicesApi, AssemblyApi, AttributionApi, AudioApi, VoicesApi } from '@app/api';
import { override, resetServices, use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';
import type { StreamKind } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { settleMicrotasks } from '../../testing/fake-timers';
import { ActivityStore } from './activity-store';
import './activity-drawer';
import type { ActivityDrawer } from './activity-drawer';

/** Enough of LiveService for the store, the feeds and the services tab (ported case for case). */
class FakeLive {
  readonly queue = signal(null);
  readonly assembly = signal(IDLE_ASSEMBLY);
  readonly voiceBatch = signal(IDLE_VOICE_BATCH);
  readonly watchdog = signal({ llama: 'serviceHealthy' });
  readonly serviceStatus = signal({ llama: 'Ready' });
  readonly throughput = signal(null);
  readonly joined: StreamKind[] = [];
  readonly left: StreamKind[] = [];

  on() {
    return () => undefined;
  }
  resynced() {
    return () => undefined;
  }
  async joinStream(kind: StreamKind) {
    this.joined.push(kind);
  }
  async leaveStream(kind: StreamKind) {
    this.left.push(kind);
  }
}

let live: FakeLive;

async function setup() {
  const noop = { cancel: mock(), dismiss: mock(), cancelBatch: mock() };
  override(LiveService, live as unknown as LiveService);
  override(ToastService, {
    success: mock(),
    info: mock(),
    error: mock(),
  } as unknown as ToastService);
  override(AttributionApi, noop as unknown as AttributionApi);
  override(AudioApi, noop as unknown as AudioApi);
  override(VoicesApi, noop as unknown as VoicesApi);
  override(AssemblyApi, noop as unknown as AssemblyApi);
  override(AiServicesApi, {
    statusAll: async () => [],
    list: async () => [
      { name: 'llama', containerName: 'read2me-llama', baseUrl: '', usesGpu: true },
    ],
  } as unknown as AiServicesApi);
  const store = use(ActivityStore);
  const el = document.createElement('r2m-activity-drawer') as ActivityDrawer;
  document.body.append(el);
  await el.rendered();
  return { store, el };
}

beforeEach(() => {
  resetServices();
  live = new FakeLive();
});
afterEach(() => document.body.replaceChildren());

describe('r2m-activity-drawer', () => {
  it('opens on the Jobs tab with the empty state and no stream joined', async () => {
    const { el } = await setup();
    expect(el.querySelector('.activity-jobs')).not.toBeNull();
    expect(el.textContent).toContain('No background work');
    expect(live.joined).toEqual([]);
  });

  it('joins a stream while its tab is visible and leaves when another tab is chosen', async () => {
    const { store, el } = await setup();

    store.tab.set('llm');
    await el.rendered();
    expect(el.querySelector('r2m-llm-stream-tab')).not.toBeNull();
    expect(live.joined).toEqual(['llm']);

    store.tab.set('audio');
    await el.rendered();
    expect(el.querySelector('r2m-llm-stream-tab')).toBeNull();
    expect(el.querySelector('r2m-audio-stream-tab')).not.toBeNull();
    expect(live.left).toEqual(['llm']);
    expect(live.joined).toEqual(['llm', 'audio']);
  });

  it('leaves the stream when the drawer is removed (closed)', async () => {
    const { store, el } = await setup();
    store.tab.set('llm');
    await el.rendered();
    el.remove();
    expect(live.left).toEqual(['llm']);
  });

  it('lists managed services with the hub-fed status and controls on the Services tab', async () => {
    const { store, el } = await setup();
    store.tab.set('services');
    await el.rendered();
    await settleMicrotasks();
    await el.rendered();
    expect(el.textContent).toContain('read2me-llama');
    expect(el.querySelector('.r2m-docker-controls .r2m-status-chip')?.textContent).toContain(
      'Ready',
    );
    expect(el.querySelector('.r2m-docker-controls button[aria-label="Shutdown"]')).not.toBeNull();
    expect(el.querySelector('a[href="settings/services"]')).not.toBeNull();
  });

  it('renders one tab per section, selecting through the tablist', async () => {
    const { store, el } = await setup();
    const labels = Array.from(el.querySelectorAll('[role="tab"] .r2m-tabs__label')).map((a) =>
      a.textContent?.trim(),
    );
    expect(labels).toEqual(['Jobs', 'LLM', 'Audio', 'Services']);
    el.querySelector<HTMLElement>('[role="tab"][data-tab="services"]')!.click();
    expect(store.tab()).toBe('services');
  });
});
