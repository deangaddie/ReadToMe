import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { AiServiceDto } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { FakeTimers, settleMicrotasks } from '../../../../testing/fake-timers';
import { type ManagedServiceStatus, RESOLVE_DEBOUNCE_MS } from './managed-service-status';
import './managed-service-status';

const LLAMA: AiServiceDto = {
  name: 'llama',
  containerName: 'read2me-llama',
  baseUrl: 'http://localhost:8080',
  usesGpu: true,
};

let api: FakeApi;
let live: FakeLive;
let timers: FakeTimers;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  timers = new FakeTimers().install();
  override(ToastService, {
    success: () => undefined,
    error: () => undefined,
    warn: () => undefined,
    problem: () => undefined,
  } as unknown as ToastService);
  api.on('GET', '/api/ai-services/resolve', LLAMA);
  api.on('GET', '/api/ai-services/llama/status', { name: 'llama', status: 'Ready' });
  api.on('POST', '/api/ai-services/llama/restart', undefined);
});
afterEach(() => {
  timers.restore();
  document.body.replaceChildren();
});

/** Mounts the element and runs past the resolve debounce. */
async function mount(baseUrl = 'http://localhost:8080') {
  const el = document.createElement('r2m-managed-service-status') as ManagedServiceStatus;
  el.baseUrl = baseUrl;
  document.body.append(el);
  await el.rendered();
  await timers.advance(RESOLVE_DEBOUNCE_MS);
  await settleMicrotasks();
  await el.rendered();
  return el;
}

describe('r2m-managed-service-status', () => {
  it('resolves the draft URL, probes an unobserved service once and shows the hub-fed chip with controls', async () => {
    const el = await mount();
    expect(api.calls('GET', '/api/ai-services/resolve').length).toBe(1);
    expect(api.calls('GET', '/api/ai-services/llama/status').length).toBe(1);

    live.serviceStatus.set({ llama: 'Ready' });
    await el.rendered();
    expect(el.querySelector('.r2m-status-chip')?.textContent).toContain('Ready');
    expect(el.textContent).toContain('read2me-llama');

    el.querySelector<HTMLButtonElement>('button[aria-label="Restart"]')!.click();
    await settleMicrotasks();
    expect(api.calls('POST', '/api/ai-services/llama/restart').length).toBe(1);
  });

  it('renders nothing for a URL the watchdog does not manage', async () => {
    api.on('GET', '/api/ai-services/resolve', problem(404, 'not managed'));
    const el = await mount();
    expect(el.querySelector('.r2m-docker-controls')).toBeNull();
    expect(api.calls('GET', '/api/ai-services/llama/status').length).toBe(0);
  });

  it('asks once per settled URL, and not at all for one that is not absolute', async () => {
    const el = await mount();
    el.baseUrl = 'http://local';
    el.baseUrl = 'http://localhost:9';
    await el.rendered();
    expect(el.querySelector('.r2m-docker-controls')).toBeNull();
    await timers.advance(RESOLVE_DEBOUNCE_MS);
    await settleMicrotasks();
    expect(api.calls('GET', '/api/ai-services/resolve').length).toBe(2);

    el.baseUrl = 'nowhere';
    await timers.advance(RESOLVE_DEBOUNCE_MS);
    expect(api.calls('GET', '/api/ai-services/resolve').length).toBe(2);
  });
});
