import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  AttributionChainResponse,
  AttributionPromptStyle,
  LlmApiType,
  LlmServerConfig,
} from '@app/api';
import { LlmSettingsPage } from './llm-settings-page';

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

describe('LlmSettingsPage', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LlmSettingsPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  async function eventually(method: string, url: string): Promise<TestRequest> {
    for (let i = 0; i < 100; i++) {
      const [match] = http.match((r) => r.method === method && r.urlWithParams === url);
      if (match) return match;
      await settle(10);
    }
    throw new Error(`No ${method} ${url}`);
  }

  /** Answers one store load. */
  async function flushLoad(
    configs: LlmServerConfig[],
    active: LlmServerConfig | null,
    c = chain(),
  ) {
    (await eventually('GET', '/api/settings/llm')).flush(configs);
    const activeReq = await eventually('GET', '/api/settings/llm/active');
    if (active) activeReq.flush(active);
    else activeReq.flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    (await eventually('GET', '/api/settings/llm/attribution-chain')).flush(c);
  }

  /** The editor resolves its base URL to a managed service on open; none of these are managed. */
  async function flushResolve(baseUrl: string) {
    (await eventually('GET', `/api/ai-services/resolve?baseUrl=${baseUrl}`)).flush(
      { title: 'Not Found' },
      { status: 404, statusText: 'Not Found' },
    );
  }

  async function render(c = chain()) {
    const fixture = TestBed.createComponent(LlmSettingsPage);
    await flushLoad([SMALL, BIG], SMALL, c);
    await flushResolve(SMALL.baseUrl);
    (await eventually('GET', '/api/settings/llm/test')).flush({ running: false, configId: null });
    await stable(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  async function stable(fixture: ComponentFixture<unknown>) {
    await settle();
    await fixture.whenStable();
  }

  function type(el: HTMLElement, field: string, value: string) {
    const input = el.querySelector<HTMLInputElement>(`[data-field="${field}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  const button = (el: HTMLElement, label: string) =>
    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!;

  it('lists the configs, marks the default and opens it in the editor', async () => {
    const { el } = await render();

    const rows = Array.from(el.querySelectorAll('.r2m-config-list__row'));
    expect(rows.map((r) => r.querySelector('.r2m-config-list__name')?.textContent)).toEqual([
      'small',
      'big',
    ]);
    expect(rows[0]!.textContent).toContain('Default');
    expect(rows[1]!.textContent).not.toContain('Default');
    expect(el.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe('small');
    expect(el.querySelector<HTMLInputElement>('[data-field="baseUrl"]')?.value).toBe(SMALL.baseUrl);
    expect(el.querySelector('app-llm-test-console')?.textContent).toContain('Test "small"');
  });

  it('Save shows the validation message and sends nothing until the form is valid', async () => {
    const { fixture, el } = await render();

    type(el, 'baseUrl', 'not a url');
    await stable(fixture);
    button(el, 'Save').click();
    await stable(fixture);
    expect(el.querySelector('.llm-editor__error')?.textContent).toBe(
      'Base URL must be a valid absolute URL (e.g. http://localhost:8080).',
    );

    type(el, 'baseUrl', 'http://llm-1:9090');
    type(el, 'temperature', '0.4');
    await stable(fixture);
    button(el, 'Save').click();
    const put = await eventually('PUT', '/api/settings/llm/1');
    expect(put.request.body).toMatchObject({
      id: 1,
      baseUrl: 'http://llm-1:9090',
      temperature: 0.4,
    });
    const saved = { ...SMALL, baseUrl: 'http://llm-1:9090', temperature: 0.4 };
    put.flush(saved);
    await flushLoad([saved, BIG], saved);
    await flushResolve('http://llm-1:9090');
    await stable(fixture);

    expect(el.querySelector('.llm-editor__error')).toBeNull();
    expect(el.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
  });

  it('Get models failure leaves free text with the hint', async () => {
    const { fixture, el } = await render();

    el.querySelector<HTMLButtonElement>('[data-action="get-models"]')!.click();
    (await eventually('POST', '/api/settings/llm/models')).flush(
      { title: 'Unprocessable', detail: 'Connection refused' },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    await stable(fixture);

    expect(el.querySelector('[data-role="models-hint"]')?.textContent).toBe(
      'Could not fetch models (Connection refused) — type one manually.',
    );
    expect(el.querySelector('input[data-field="model"]')).not.toBeNull();
  });

  it('moving a chain step down PUTs the reordered chain', async () => {
    const steps = [
      { configId: 1, thinking: false, promptStyle: AttributionPromptStyle.Simple },
      { configId: 2, thinking: true, promptStyle: AttributionPromptStyle.Full },
    ];
    const { fixture, el } = await render(chain({ steps }));

    expect(
      Array.from(el.querySelectorAll('.chain__step')).map((s) => s.textContent?.includes('Simple')),
    ).toEqual([true, false]);
    el.querySelector<HTMLButtonElement>('[aria-label="Move small down"]')!.click();

    const put = await eventually('PUT', '/api/settings/llm/attribution-chain');
    expect(put.request.body).toEqual({ steps: [steps[1], steps[0]], selfConsistency: false });
    put.flush(chain({ steps: [steps[1]!, steps[0]!] }));
    await stable(fixture);

    expect(Array.from(el.querySelectorAll('.chain__name')).map((n) => n.textContent)).toEqual([
      'big',
      'small',
    ]);
  });

  it('names the fallback when the chain is empty', async () => {
    const { el } = await render(
      chain({ resolved: [{ config: SMALL, thinking: false, promptStyle: SMALL.promptStyle }] }),
    );

    expect(el.querySelector('.chain__alert--info')?.textContent).toContain('small');
  });

  it('switching config with unsaved edits asks before discarding them', async () => {
    const { fixture, el } = await render();
    type(el, 'name', 'renamed');
    await stable(fixture);
    expect(fixture.componentInstance.hasUnsavedChanges()).toBe(true);

    Array.from(el.querySelectorAll<HTMLButtonElement>('.r2m-config-list__main'))[1]!.click();
    await stable(fixture);
    const dialog = document.querySelector('r2m-confirm-dialog')!;
    expect(dialog.textContent).toContain('unsaved changes');
    button(dialog as HTMLElement, 'Cancel').click();
    await settle(300);
    await stable(fixture);

    expect(el.querySelector<HTMLInputElement>('[data-field="name"]')?.value).toBe('renamed');
  });
});
