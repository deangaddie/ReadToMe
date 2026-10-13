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

/** A row's popover menu entry (after its icon); happy-dom has no popover API, so it is clicked in place. */
const rowAction = (page: Element, name: string, action: string) =>
  Array.from(
    page.querySelectorAll<HTMLButtonElement>(
      `[role="menu"][aria-label="Actions for ${name}"] [role="menuitem"]`,
    ),
  ).find((b) => b.textContent?.trim().endsWith(action))!;

const addStepItem = (page: Element, label: string) =>
  Array.from(
    page.querySelectorAll<HTMLButtonElement>('#r2m-llm-chain-add-menu [role="menuitem"]'),
  ).find((b) => b.textContent?.trim() === label)!;

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

  it('New → Save POSTs the config and selects the created one', async () => {
    const page = await render();
    page.querySelector<HTMLButtonElement>('.r2m-config-list__head .r2m-button--filled')!.click();
    await flush(page);
    expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe(
      'New configuration',
    );

    await type(page, 'name', 'fresh');
    await type(page, 'baseUrl', 'http://fresh:8080');
    await type(page, 'model', 'fresh-model');
    const FRESH = config(3, 'fresh', 'http://fresh:8080');
    api.on('POST', LIST, FRESH);
    routeLoad([SMALL, BIG, FRESH], SMALL);
    button(page, 'Save').click();
    await flush(page);

    expect(api.calls('POST', LIST)[0]?.body).toMatchObject({
      id: 0,
      name: 'fresh',
      baseUrl: 'http://fresh:8080',
      model: 'fresh-model',
    });
    expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe('fresh');
    expect(
      page.querySelector('.r2m-config-list__row--selected .r2m-config-list__name')?.textContent,
    ).toBe('fresh');
    expect(toasts).toEqual(['Configuration created']);
  });

  it('Make default PUTs /active and moves the Default marker', async () => {
    const page = await render();
    api.on('PUT', ACTIVE, undefined);
    routeLoad([SMALL, BIG], BIG);

    rowAction(page, 'big', 'Make default').click();
    await flush(page);

    expect(api.calls('PUT', ACTIVE).map((c) => c.body)).toEqual([{ id: 2 }]);
    const r = rows(page);
    expect(r[0]!.textContent).not.toContain('Default');
    expect(r[1]!.textContent).toContain('Default');
  });

  it('Delete asks a destructive confirm, then DELETEs and drops the row', async () => {
    const page = await render();
    api.on('DELETE', `${LIST}/2`, undefined);

    confirmAnswer = false;
    rowAction(page, 'big', 'Delete').click();
    await flush(page);
    expect(confirms[0]).toMatchObject({ title: 'Delete big?', destructive: true });
    expect(api.calls('DELETE', `${LIST}/2`)).toHaveLength(0);

    confirmAnswer = true;
    routeLoad([SMALL], SMALL);
    rowAction(page, 'big', 'Delete').click();
    await flush(page);

    expect(api.calls('DELETE', `${LIST}/2`)).toHaveLength(1);
    expect(
      rows(page).map((row) => row.querySelector('.r2m-config-list__name')?.textContent),
    ).toEqual(['small']);
    expect(toasts).toEqual(['Deleted big']);
  });

  it.each([
    {
      label: 'small',
      step: { configId: 1, thinking: false, promptStyle: AttributionPromptStyle.Full },
    },
    {
      label: 'small (simple)',
      step: { configId: 1, thinking: false, promptStyle: AttributionPromptStyle.Simple },
    },
    {
      label: 'small (chapter)',
      step: { configId: 1, thinking: false, promptStyle: AttributionPromptStyle.Chapter },
    },
    {
      label: 'big (thinking)',
      step: { configId: 2, thinking: true, promptStyle: AttributionPromptStyle.Full },
    },
    {
      label: 'big (simple, thinking)',
      step: { configId: 2, thinking: true, promptStyle: AttributionPromptStyle.Simple },
    },
  ])('Add step "$label" PUTs the chain with that variant appended', async ({ label, step }) => {
    const existing = { configId: 2, thinking: false, promptStyle: AttributionPromptStyle.Chapter };
    const page = await render(chain({ steps: [existing] }));
    api.on('PUT', CHAIN, (body) => chain(body as Partial<AttributionChainResponse>));

    addStepItem(page, label).click();
    await flush(page);

    expect(api.calls('PUT', CHAIN).map((c) => c.body)).toEqual([
      { steps: [existing, step], selfConsistency: false },
    ]);
    expect(page.querySelectorAll('.chain__step')).toHaveLength(2);
  });

  it('offers Chapter only with thinking off', async () => {
    const page = await render();
    const labels = Array.from(
      page.querySelectorAll('#r2m-llm-chain-add-menu [role="menuitem"]'),
      (b) => b.textContent?.trim(),
    );
    expect(labels).toContain('small (chapter)');
    expect(labels.filter((l) => l?.includes('chapter, thinking'))).toEqual([]);
  });

  it('the self-consistency switch PUTs and stays disabled until the PUT answers', async () => {
    const page = await render();
    let answer!: (value: AttributionChainResponse) => void;
    api.on('PUT', CHAIN, () => new Promise<AttributionChainResponse>((r) => (answer = r)));
    const toggle = () => page.querySelector<HTMLInputElement>('.chain__self input[role="switch"]')!;
    expect(toggle().checked).toBe(false);

    toggle().checked = true;
    toggle().dispatchEvent(new Event('change'));
    await flush(page);

    expect(api.calls('PUT', CHAIN).map((c) => c.body)).toEqual([
      { steps: [], selfConsistency: true },
    ]);
    expect(toggle().disabled).toBe(true);

    answer(chain({ selfConsistency: true }));
    await flush(page);
    expect(toggle().disabled).toBe(false);
    expect(toggle().checked).toBe(true);
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
