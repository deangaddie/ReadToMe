import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import {
  type AttributionChainResponse,
  AttributionPromptStyle,
  LlmApiType,
  type LlmServerConfig,
} from '@app/api';
import { override, resetServices } from '@app/core/services';
import { ConfirmService, type ConfirmOptions } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import type { LlmSettingsPage } from './llm-settings-page';
import './llm-settings-page';

/**
 * Ported from the Angular TestBed spec, case for case: the page is created, appended, and its
 * HTTP goes through `FakeApi`; the hub is `FakeLive`; the discard prompt is a recorded stub.
 */
function config(id: number, name: string, baseUrl = `http://llm-${id}:8080`): LlmServerConfig {
  return {
    id,
    name,
    apiType: LlmApiType.OpenAiCompatible,
    baseUrl,
    apiKey: null,
    model: `${name}-model`,
    temperature: null,
    topP: null,
    maxTokens: null,
    frequencyPenalty: null,
    presencePenalty: null,
    attributionBatchSize: 1,
    promptStyle: AttributionPromptStyle.Full,
    supportsModelSwitch: false,
  };
}

const SMALL = config(1, 'small');
const BIG = config(2, 'big');

function chain(overrides: Partial<AttributionChainResponse> = {}): AttributionChainResponse {
  return { steps: [], selfConsistency: false, resolved: [], available: [SMALL, BIG], ...overrides };
}

const LIST = '/api/settings/llm';
const ACTIVE = '/api/settings/llm/active';
const CHAIN = '/api/settings/llm/attribution-chain';

let api: FakeApi;
let live: FakeLive;
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;
let toasts: string[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  confirms = [];
  confirmAnswer = true;
  toasts = [];
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    error: (m: string) => toasts.push(`error: ${m}`),
    warn: () => undefined,
    info: () => undefined,
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

/** Routes one store load; the editor's managed-service resolve and the console's read-back 404 harmlessly. */
function routeLoad(configs: LlmServerConfig[], active: LlmServerConfig | null, c = chain()) {
  api.on('GET', LIST, configs);
  api.on('GET', ACTIVE, active ?? problem(404, 'none'));
  api.on('GET', CHAIN, c);
}

async function flush(page: LlmSettingsPage) {
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
  await page.rendered();
}

async function render(c = chain()) {
  routeLoad([SMALL, BIG], SMALL, c);
  api.on('GET', '/api/settings/llm/test', { running: false, configId: null });
  const page = document.createElement('r2m-llm-settings-page') as LlmSettingsPage;
  document.body.append(page);
  await flush(page);
  return page;
}

async function type(page: LlmSettingsPage, field: string, value: string) {
  const input = page.querySelector<HTMLInputElement>(`[data-field="${field}"]`)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await page.rendered();
}

const button = (el: Element, label: string) =>
  Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!;

const rows = (page: Element) => Array.from(page.querySelectorAll('.r2m-config-list__row'));

describe('r2m-llm-settings-page', () => {
  it('lists the configs, marks the default and opens it in the editor', async () => {
    const page = await render();

    const r = rows(page);
    expect(r.map((row) => row.querySelector('.r2m-config-list__name')?.textContent)).toEqual([
      'small',
      'big',
    ]);
    expect(r[0]!.textContent).toContain('Default');
    expect(r[1]!.textContent).not.toContain('Default');
    expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe('small');
    expect(page.querySelector<HTMLInputElement>('[data-field="baseUrl"]')?.value).toBe(
      SMALL.baseUrl,
    );
    expect(page.querySelector('r2m-llm-test-console')?.textContent).toContain('Test "small"');
  });

  it('Save shows the validation message and sends nothing until the form is valid', async () => {
    const page = await render();

    await type(page, 'baseUrl', 'not a url');
    button(page, 'Save').click();
    await page.rendered();
    expect(page.querySelector('.llm-editor__error')?.textContent?.trim()).toBe(
      'Base URL must be a valid absolute URL (e.g. http://localhost:8080).',
    );
    expect(api.calls('PUT', `${LIST}/1`).length).toBe(0);

    await type(page, 'baseUrl', 'http://llm-1:9090');
    await type(page, 'temperature', '0.4');
    const saved = { ...SMALL, baseUrl: 'http://llm-1:9090', temperature: 0.4 };
    api.on('PUT', `${LIST}/1`, saved);
    routeLoad([saved, BIG], saved);
    button(page, 'Save').click();
    await flush(page);

    expect(api.calls('PUT', `${LIST}/1`)[0]?.body).toMatchObject({
      id: 1,
      baseUrl: 'http://llm-1:9090',
      temperature: 0.4,
    });
    expect(page.querySelector('.llm-editor__error')).toBeNull();
    expect(page.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
    expect(toasts).toEqual(['Configuration updated']);
  });

  it('Get models failure leaves free text with the hint', async () => {
    const page = await render();
    api.on('POST', `${LIST}/models`, problem(422, 'Connection refused'));

    page.querySelector<HTMLButtonElement>('[data-action="get-models"]')!.click();
    await flush(page);

    expect(page.querySelector('[data-role="models-hint"]')?.textContent).toBe(
      'Could not fetch models (Connection refused) — type one manually.',
    );
    expect(page.querySelector('input[data-field="model"]')).not.toBeNull();
  });

  it('Get models success offers the list in a select, keeping the current model', async () => {
    const page = await render();
    api.on('POST', `${LIST}/models`, { models: ['qwen-28b', 'gemma-12b'] });

    page.querySelector<HTMLButtonElement>('[data-action="get-models"]')!.click();
    await flush(page);

    const select = page.querySelector<HTMLSelectElement>('select[data-field="model"]')!;
    expect(Array.from(select.options).map((o) => o.value)).toEqual([
      '',
      'small-model',
      'qwen-28b',
      'gemma-12b',
    ]);
    expect(select.value).toBe('small-model');
  });

  it('moving a chain step down PUTs the reordered chain', async () => {
    const steps = [
      { configId: 1, thinking: false, promptStyle: AttributionPromptStyle.Simple },
      { configId: 2, thinking: true, promptStyle: AttributionPromptStyle.Full },
    ];
    const page = await render(chain({ steps }));

    expect(
      Array.from(page.querySelectorAll('.chain__step')).map((s) =>
        s.textContent?.includes('Simple'),
      ),
    ).toEqual([true, false]);
    api.on('PUT', CHAIN, chain({ steps: [steps[1]!, steps[0]!] }));
    page.querySelector<HTMLButtonElement>('[aria-label="Move small down"]')!.click();
    await flush(page);

    expect(api.calls('PUT', CHAIN)[0]?.body).toEqual({
      steps: [steps[1], steps[0]],
      selfConsistency: false,
    });
    expect(Array.from(page.querySelectorAll('.chain__name')).map((n) => n.textContent)).toEqual([
      'big',
      'small',
    ]);
  });

  it('a Chapter rung shows the Chapter chip', async () => {
    const steps = [
      { configId: 1, thinking: false, promptStyle: AttributionPromptStyle.Chapter },
      { configId: 2, thinking: true, promptStyle: AttributionPromptStyle.Full },
    ];
    const page = await render(chain({ steps }));

    expect(
      Array.from(page.querySelectorAll('.chain__step')).map((s) =>
        Array.from(s.querySelectorAll('.r2m-status-chip__label')).map((c) => c.textContent?.trim()),
      ),
    ).toEqual([['Chapter'], ['Thinking']]);
  });

  it('names the fallback when the chain is empty', async () => {
    const page = await render(
      chain({ resolved: [{ config: SMALL, thinking: false, promptStyle: SMALL.promptStyle }] }),
    );

    expect(page.querySelector('.chain__alert--info')?.textContent).toContain('small');
  });

  it('switching config with unsaved edits asks before discarding them', async () => {
    const page = await render();
    await type(page, 'name', 'renamed');
    expect(page.hasUnsavedChanges()).toBe(true);

    confirmAnswer = false;
    Array.from(page.querySelectorAll<HTMLButtonElement>('.r2m-config-list__main'))[1]!.click();
    await flush(page);
    expect(confirms[0]?.message).toContain('unsaved changes');

    expect(page.querySelector<HTMLInputElement>('[data-field="name"]')?.value).toBe('renamed');
    expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe('small');
  });

  it('reloads on settingsChanged for llm and follows a config edited elsewhere', async () => {
    const page = await render();
    routeLoad([{ ...SMALL, model: 'elsewhere' }, BIG], SMALL);

    live.emit('settingsChanged', { area: 'prompts' });
    await flush(page);
    expect(api.calls('GET', LIST).length).toBe(1);

    live.emit('settingsChanged', { area: 'llm' });
    await flush(page);
    expect(api.calls('GET', LIST).length).toBe(2);
    expect(page.querySelector<HTMLInputElement>('[data-field="model"]')?.value).toBe('elsewhere');
    expect(page.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
  });
});
