import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { AttributionPromptStyle, LlmApiType, type LlmServerConfig } from '@app/api';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import type { LlmTestConsole } from './llm-test-console';
import './llm-test-console';

const FAKE: LlmServerConfig = {
  id: 7,
  name: 'fake',
  apiType: LlmApiType.OpenAiCompatible,
  baseUrl: 'http://fake-llm',
  apiKey: null,
  model: null,
  temperature: null,
  topP: null,
  maxTokens: null,
  frequencyPenalty: null,
  presencePenalty: null,
  attributionBatchSize: 1,
  promptStyle: AttributionPromptStyle.Full,
  supportsModelSwitch: false,
};

const STATUS = '/api/settings/llm/test';
const START = '/api/settings/llm/7/test';
const CANCEL = '/api/settings/llm/7/test/cancel';

let api: FakeApi;
let live: FakeLive;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  api.on('GET', STATUS, { running: false, configId: null });
  api.on('POST', START, undefined);
  api.on('POST', CANCEL, undefined);
});
afterEach(() => document.body.replaceChildren());

async function flush(el: LlmTestConsole) {
  for (let i = 0; i < 4; i++) await settle();
  await el.rendered();
}

async function mount() {
  const el = document.createElement('r2m-llm-test-console') as LlmTestConsole;
  el.config = FAKE;
  document.body.append(el);
  await flush(el);
  return el;
}

const send = (el: Element) => el.querySelector<HTMLButtonElement>('[data-action="send"]')!;
const stop = (el: Element) => el.querySelector<HTMLButtonElement>('[data-action="stop"]');

async function typePrompt(el: LlmTestConsole, text: string) {
  const area = el.querySelector<HTMLTextAreaElement>('[data-field="prompt"]')!;
  area.value = text;
  area.dispatchEvent(new Event('input'));
  await el.rendered();
}

describe('r2m-llm-test-console', () => {
  it('joins the LLM stream while on screen and reads the run state back on mount', async () => {
    const el = await mount();
    expect(live.joinedStreams).toEqual(['llm']);
    expect(api.calls('GET', STATUS).length).toBe(1);
    expect(el.textContent).toContain('Test "fake"');
    expect(send(el).disabled).toBe(true);

    el.remove();
    expect(live.leftStreams).toEqual(['llm']);
  });

  it('does not read the run state again when the hub first connects after the console opened', async () => {
    live.state.set('connecting');
    await mount();
    live.state.set('connected');
    live.resync();
    await settle();
    expect(api.calls('GET', STATUS).length).toBe(1);
  });

  it('reads the run state back on the first connect while a run shows, since its ending may be lost', async () => {
    live.state.set('connecting');
    api.on('GET', STATUS, { running: true, configId: 7 });
    const el = await mount();
    expect(stop(el)).not.toBeNull();

    api.on('GET', STATUS, { running: false, configId: null });
    live.state.set('connected');
    live.resync();
    await flush(el);
    expect(api.calls('GET', STATUS).length).toBe(2);
    expect(stop(el)).toBeNull();
  });

  it('reads the run state back on a resync after the hub was lost, once per loss', async () => {
    await mount();
    live.state.set('reconnecting');
    live.state.set('connected');
    live.resync();
    await settle();
    expect(api.calls('GET', STATUS).length).toBe(2);

    live.resync();
    await settle();
    expect(api.calls('GET', STATUS).length).toBe(2);

    live.state.set('disconnected');
    live.state.set('connected');
    live.resync();
    await settle();
    expect(api.calls('GET', STATUS).length).toBe(3);
  });

  it('sends the prompt with this connection id, streams until llmTest ends it', async () => {
    const el = await mount();
    await typePrompt(el, 'Say something');
    expect(send(el).disabled).toBe(false);

    send(el).click();
    await flush(el);
    expect(api.calls('POST', START)[0]?.body).toEqual({
      prompt: 'Say something',
      connectionId: 'fake-connection',
    });
    expect(stop(el)).not.toBeNull();
    expect(el.querySelector<HTMLTextAreaElement>('[data-field="prompt"]')?.disabled).toBe(true);

    live.emit('llmTest', { kind: 'failed', configId: 7, reason: 'boom' });
    await el.rendered();
    expect(stop(el)).toBeNull();
    expect(el.querySelector('.console__error')?.textContent).toContain('Request failed: boom');
  });

  it('Stop cancels the run, and a 409 on send follows the run already going', async () => {
    const el = await mount();
    await typePrompt(el, 'Take your time');
    send(el).click();
    await flush(el);
    stop(el)!.click();
    await flush(el);
    expect(api.calls('POST', CANCEL).length).toBe(1);

    live.emit('llmTest', { kind: 'cancelled', configId: 7 });
    await el.rendered();
    api.on('POST', START, problem(409, 'already running'));
    send(el).click();
    await flush(el);
    expect(stop(el)).not.toBeNull();
    expect(el.querySelector('.console__error')).toBeNull();
  });

  it('a run ending without an llmTest message is read back from the host', async () => {
    const el = await mount();
    await typePrompt(el, 'x');
    send(el).click();
    await flush(el);
    api.on('GET', STATUS, { running: false, configId: null });

    live.emit('llm', { kind: 'runEnded' });
    await flush(el);
    expect(api.calls('GET', STATUS).length).toBe(2);
    expect(stop(el)).toBeNull();
  });
});
