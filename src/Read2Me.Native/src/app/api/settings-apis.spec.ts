import { beforeEach, describe, expect, it } from 'bun:test';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../testing/fake-api';
import { PromptsApi } from './prompts-api';
import { LlmSettingsApi, ParagraphTtsSettingsApi } from './settings-api';

/**
 * The settings cases of Angular's `api-services.spec.ts` (ported with the screens that use them),
 * plus the LLM page's own calls.
 */
let api: FakeApi;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
});

describe('settings API services', () => {
  it('SettingsApi lists, reads a missing active config as null and sets the active one', async () => {
    api.on('GET', '/api/settings/llm', []);
    api.on('GET', '/api/settings/paragraph-tts/active', problem(404, 'none'));
    api.on('PUT', '/api/settings/llm/active', undefined);
    const llm = new LlmSettingsApi();

    expect(await llm.list()).toEqual([]);
    expect(await new ParagraphTtsSettingsApi().active()).toBeNull();
    await llm.setActive(7);
    expect(api.calls('PUT', '/api/settings/llm/active')[0]?.body).toEqual({ id: 7 });
  });

  it('LlmSettingsApi.models posts the unsaved config and unwraps the ids', async () => {
    api.on('POST', '/api/settings/llm/models', { models: ['qwen-28b'] });
    const config = { id: 0, name: 'x' } as never;

    expect(await new LlmSettingsApi().models(config)).toEqual(['qwen-28b']);
    expect(api.calls('POST', '/api/settings/llm/models')[0]?.body).toEqual({ id: 0, name: 'x' });
  });

  it('LlmSettingsApi test calls address the config; status is global', async () => {
    api.on('POST', '/api/settings/llm/3/test', undefined);
    api.on('POST', '/api/settings/llm/3/test/cancel', undefined);
    api.on('GET', '/api/settings/llm/test', { running: true, configId: 3 });
    const llm = new LlmSettingsApi();

    await llm.startTest(3, { prompt: 'hi', connectionId: 'c1' });
    await llm.cancelTest(3);
    expect(await llm.testStatus()).toEqual({ running: true, configId: 3 });
    expect(api.calls('POST', '/api/settings/llm/3/test')[0]?.body).toEqual({
      prompt: 'hi',
      connectionId: 'c1',
    });
    expect(api.calls('POST', '/api/settings/llm/3/test/cancel').length).toBe(1);
  });

  it('LlmSettingsApi.setAttributionChain puts the whole chain and returns the stored one', async () => {
    const stored = { steps: [], selfConsistency: true, resolved: [], available: [] };
    api.on('PUT', '/api/settings/llm/attribution-chain', stored);
    const request = {
      steps: [{ configId: 1, thinking: true, promptStyle: null }],
      selfConsistency: true,
    };

    expect(await new LlmSettingsApi().setAttributionChain(request)).toEqual(stored);
    expect(api.calls('PUT', '/api/settings/llm/attribution-chain')[0]?.body).toEqual(request);
  });

  it('PromptsApi.set puts the template by kind', async () => {
    api.on('PUT', '/api/settings/prompts/voice-plan', undefined);
    await new PromptsApi().set('voice-plan', 'Plan {{name}}');
    expect(api.calls('PUT', '/api/settings/prompts/voice-plan')[0]?.body).toEqual({
      template: 'Plan {{name}}',
    });
  });

  it('PromptsApi.catalog gets the catalog', async () => {
    api.on('GET', '/api/settings/prompts/catalog', [
      { kind: 'voice', title: 'Character Voice Prompt' },
    ]);
    expect(await new PromptsApi().catalog()).toEqual([
      { kind: 'voice', title: 'Character Voice Prompt' },
    ] as never);
  });

  it('PromptsApi.preview posts the unsaved template and unwraps the rendering', async () => {
    api.on('POST', '/api/settings/prompts/voice/preview', { rendered: 'Voice for Gandalf' });
    expect(await new PromptsApi().preview('voice', 'Voice for {{character_name}}')).toBe(
      'Voice for Gandalf',
    );
    expect(api.calls('POST', '/api/settings/prompts/voice/preview')[0]?.body).toEqual({
      template: 'Voice for {{character_name}}',
    });
  });

  it('PromptsApi.reset deletes the override', async () => {
    api.on('DELETE', '/api/settings/prompts/voice', undefined);
    await new PromptsApi().reset('voice');
    expect(api.calls('DELETE', '/api/settings/prompts/voice').length).toBe(1);
  });
});
