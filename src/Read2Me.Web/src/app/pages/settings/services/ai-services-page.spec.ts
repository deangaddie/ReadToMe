import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { type AiServiceDto, AiServicesApi } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import type { AiServicesPage } from './ai-services-page';
import { formatLogTime } from './ai-services-page';
import './ai-services-page';

/** Ported from the Angular TestBed spec, case for case, over a recorded `AiServicesApi` and `FakeLive`. */
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

let live: FakeLive;
let calls: string[];

beforeEach(() => {
  resetServices();
  live = new FakeLive().install();
  calls = [];
  const record = (what: string) => async (name?: string) => {
    calls.push(name ? `${what}:${name}` : what);
  };
  override(AiServicesApi, {
    list: async () => CATALOG,
    statusAll: async () => {
      calls.push('statusAll');
      return [];
    },
    status: record('status'),
    start: record('start'),
    restart: record('restart'),
    shutdown: record('shutdown'),
  } as unknown as AiServicesApi);
  override(ToastService, {
    success: () => undefined,
    error: () => undefined,
    warn: () => undefined,
    problem: () => undefined,
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

async function flush(page: AiServicesPage) {
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
  await page.rendered();
}

async function mount() {
  const page = document.createElement('r2m-ai-services-page') as AiServicesPage;
  document.body.append(page);
  await flush(page);
  return page;
}

const chip = (card: Element | null | undefined) =>
  card?.querySelector('.r2m-docker-controls .r2m-status-chip')?.textContent;

describe('r2m-ai-services-page', () => {
  it('renders a card per service with the GPU chip and hub-fed status', async () => {
    live.serviceStatus.set({ llama: 'Ready', whisper: 'Stopped' });
    const page = await mount();

    const cards = Array.from(page.querySelectorAll('.services-page__card'));
    expect(cards.map((c) => c.getAttribute('data-service'))).toEqual(['llama', 'whisper']);
    expect(
      cards[0]?.querySelector('.services-page__card-head .r2m-status-chip')?.textContent,
    ).toContain('GPU');
    expect(cards[1]?.querySelector('.services-page__card-head .r2m-status-chip')).toBeNull();
    expect(chip(cards[0])).toContain('Ready');
    expect(chip(cards[1])).toContain('Stopped');
    expect(cards[0]?.textContent).toContain('read2me-llama');
    expect(calls.filter((c) => c === 'statusAll')).toHaveLength(1);
  });

  it('a status flip over the hub re-renders the chip, and the buttons drive the store', async () => {
    live.serviceStatus.set({ llama: 'Ready' });
    const page = await mount();
    const llama = () => page.querySelector('[data-service="llama"]')!;

    llama().querySelector<HTMLButtonElement>('button[aria-label="Shutdown"]')!.click();
    await flush(page);
    expect(calls).toContain('shutdown:llama');
    expect(llama().querySelectorAll('button:disabled')).toHaveLength(4);

    live.serviceStatus.set({ llama: 'Stopped' });
    live.emit('serviceStatus', { name: 'llama', status: 'Stopped', op: 'shutdown', ok: true });
    await flush(page);
    expect(chip(llama())).toContain('Stopped');
    expect(llama().querySelector<HTMLButtonElement>('button[aria-label="Start"]')!.disabled).toBe(
      false,
    );
  });

  it('lists watchdog events newest first with their labels', async () => {
    const page = await mount();
    live.emit('watchdog', {
      kind: 'recoveryStarted',
      service: 'llama',
      reason: 'health timed out',
    });
    live.emit('watchdog', { kind: 'serviceHealthy', service: 'llama' });
    await flush(page);
    const rows = Array.from(page.querySelectorAll('.services-page__log-row'));
    expect(rows.map((r) => r.getAttribute('data-kind'))).toEqual([
      'serviceHealthy',
      'recoveryStarted',
    ]);
    expect(rows[1]?.textContent).toContain('Recovery started');
    expect(rows[1]?.textContent).toContain('health timed out');
    expect(rows[1]?.querySelector('time')?.textContent).toMatch(/^\d\d:\d\d:\d\d$/);
  });

  it('Refresh all probes every service in one request', async () => {
    const page = await mount();
    page.querySelector<HTMLButtonElement>('[data-action="refresh-all"]')!.click();
    await flush(page);
    expect(calls.filter((c) => c === 'statusAll')).toHaveLength(2);
  });

  it('formatLogTime is the zero-padded local wall clock', () => {
    const at = new Date(2026, 9, 10, 7, 5, 9);
    expect(formatLogTime(at)).toBe('07:05:09');
  });
});
