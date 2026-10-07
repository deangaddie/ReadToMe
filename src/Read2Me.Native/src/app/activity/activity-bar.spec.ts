import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { AssemblyApi, AttributionApi, AudioApi, VoicesApi } from '@app/api';
import { override, resetServices, use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';
import type { QueueMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { ActivityStore } from './activity-store';
import { summaryJob } from './activity-bar';
import './activity-bar';
import type { ActivityBar } from './activity-bar';

class FakeLive {
  readonly queue = signal<QueueMessage | null>(null);
  readonly assembly = signal(IDLE_ASSEMBLY);
  readonly voiceBatch = signal({ ...IDLE_VOICE_BATCH });
  readonly watchdog = signal({});
  readonly throughput = signal(null);
  on() {
    return () => undefined;
  }
  resynced() {
    return () => undefined;
  }
}

let live: FakeLive;
const cancels: string[] = [];

async function mount(narrow = false) {
  override(LiveService, live as unknown as LiveService);
  override(ToastService, {
    success: mock(),
    info: mock(),
    error: mock(),
  } as unknown as ToastService);
  const voices = { cancelBatch: mock(async () => void cancels.push('voiceBatch')) };
  override(VoicesApi, voices as unknown as VoicesApi);
  override(AttributionApi, { cancel: mock() } as unknown as AttributionApi);
  override(AudioApi, { cancel: mock() } as unknown as AudioApi);
  override(AssemblyApi, { cancel: mock() } as unknown as AssemblyApi);
  const store = use(ActivityStore);
  const bar = document.createElement('r2m-activity-bar') as ActivityBar;
  bar.narrow = narrow;
  document.body.append(bar);
  await bar.rendered();
  return { store, bar };
}

beforeEach(() => {
  resetServices();
  cancels.length = 0;
  live = new FakeLive();
});
afterEach(() => document.body.replaceChildren());

describe('summaryJob', () => {
  it('stands one pill in for every active job', () => {
    const a = { id: 'a', kind: 'audio', label: 'Audio', state: 'running' } as const;
    const b = { id: 'b', kind: 'assembly', label: 'Assembling', state: 'running' } as const;
    expect(summaryJob([a])).toBe(a);
    expect(summaryJob([a, b])).toMatchObject({
      id: 'summary',
      label: '2 jobs',
      detail: 'Audio · Assembling',
    });
  });
});

describe('r2m-activity-bar', () => {
  it('says there is no background work and toggles the drawer', async () => {
    const { store, bar } = await mount();
    expect(bar.textContent).toContain('No background work');
    const toggle = bar.querySelector<HTMLButtonElement>('[aria-label="Toggle activity drawer"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    expect(store.drawerOpen()).toBe(true);
    await bar.rendered();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
  });

  it('shows one pill per active job; a pill opens the Jobs tab and × cancels', async () => {
    const { store, bar } = await mount();
    live.voiceBatch.set({
      ...IDLE_VOICE_BATCH,
      isRunning: true,
      total: 3,
      currentOperation: 'Voice batch',
    });
    live.assembly.set({ ...IDLE_ASSEMBLY, isRunning: true, currentPhase: 'Gather' });
    await bar.rendered();
    const pills = bar.querySelectorAll('.r2m-job-pill');
    expect(pills).toHaveLength(2);
    store.tab.set('llm');
    pills[0]!.querySelector<HTMLButtonElement>('.r2m-job-pill__main')!.click();
    expect(store.drawerOpen()).toBe(true);
    expect(store.tab()).toBe('jobs');
    pills[0]!.querySelector<HTMLButtonElement>('.r2m-job-pill__cancel')!.click();
    expect(cancels).toEqual(['voiceBatch']);
  });

  it('collapses to a summary pill when narrow', async () => {
    const { bar } = await mount(true);
    live.voiceBatch.set({
      ...IDLE_VOICE_BATCH,
      isRunning: true,
      total: 3,
      currentOperation: 'Voice batch',
    });
    live.assembly.set({ ...IDLE_ASSEMBLY, isRunning: true, currentPhase: 'Gather' });
    await bar.rendered();
    expect(bar.querySelectorAll('.r2m-job-pill')).toHaveLength(1);
    expect(bar.querySelector('.r2m-job-pill__label')?.textContent).toBe('2 jobs');
  });
});
