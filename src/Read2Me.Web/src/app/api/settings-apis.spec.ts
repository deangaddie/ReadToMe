import { beforeEach, describe, expect, it } from 'bun:test';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../testing/fake-api';
import { AudioProcessingApi } from './audio-processing-api';
import { PromptsApi } from './prompts-api';
import {
  LlmSettingsApi,
  ParagraphTtsSettingsApi,
  SemanticSimilaritySettingsApi,
  TranscriptionSettingsApi,
  VoiceDesignSettingsApi,
} from './settings-api';

/**
 * The settings cases of Angular's `api-services.spec.ts` (ported with the screens that use them),
 * plus the LLM page's own calls and the provider pages' text steps, sample text and Test calls.
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

  it('ParagraphTtsSettingsApi.textSteps lists the steps of one config', async () => {
    const steps = [{ stepId: 'to-sentence-case', label: 'Sentence case', builtIn: true }];
    api.on('GET', '/api/settings/paragraph-tts/4/text-steps', steps);
    expect(await new ParagraphTtsSettingsApi().textSteps(4)).toEqual(steps as never);
  });

  it('VoiceDesignSettingsApi reads and writes the sample text and tests a prompt', async () => {
    const stored = { text: null, default: 'The default.', maxLength: 40 };
    api.on('GET', '/api/settings/voice-design/sample-text', stored);
    api.on('PUT', '/api/settings/voice-design/sample-text', { ...stored, text: 'Mine.' });
    api.on('POST', '/api/settings/voice-design/2/test', {
      audioBase64: 'UklGRg==',
      contentType: 'audio/wav',
    });
    const vd = new VoiceDesignSettingsApi();

    expect(await vd.sampleText()).toEqual(stored);
    expect((await vd.setSampleText('Mine.')).text).toBe('Mine.');
    expect(api.calls('PUT', '/api/settings/voice-design/sample-text')[0]?.body).toEqual({
      text: 'Mine.',
    });
    expect((await vd.test(2, 'A warm old man')).contentType).toBe('audio/wav');
    expect(api.calls('POST', '/api/settings/voice-design/2/test')[0]?.body).toEqual({
      prompt: 'A warm old man',
    });
  });

  it('TranscriptionSettingsApi.test posts the file as form data and unwraps the transcript', async () => {
    api.on('POST', '/api/settings/transcription/1/test', { transcript: 'hello there' });
    const clip = new File([new Uint8Array([1, 2, 3])], 'clip.wav', { type: 'audio/wav' });

    expect(await new TranscriptionSettingsApi().test(1, clip)).toBe('hello there');
    const body = api.calls('POST', '/api/settings/transcription/1/test')[0]?.body as FormData;
    expect(body).toBeInstanceOf(FormData);
    expect((body.get('file') as File).name).toBe('clip.wav');
  });

  it('SemanticSimilaritySettingsApi.test posts both texts', async () => {
    const verdict = { score: 0.9, threshold: 0.85, pass: true };
    api.on('POST', '/api/settings/semantic-similarity/1/test', verdict);

    expect(await new SemanticSimilaritySettingsApi().test(1, 'a', 'b')).toEqual(verdict);
    expect(api.calls('POST', '/api/settings/semantic-similarity/1/test')[0]?.body).toEqual({
      text1: 'a',
      text2: 'b',
    });
  });

  it('AudioProcessingApi writes scalars, pauses and one step, and probes ffmpeg', async () => {
    api.on('PUT', '/api/settings/audio-processing', { ffmpegPath: 'f' });
    api.on('PUT', '/api/settings/audio-processing/pauses', { volumeMs: 1 });
    api.on('PUT', '/api/settings/audio-processing/steps/silence-trim', { stepId: 'silence-trim' });
    api.on('POST', '/api/settings/audio-processing/ffmpeg/test', { success: true, message: 'ok' });
    const audio = new AudioProcessingApi();

    await audio.update({ werThreshold: 0.2 });
    await audio.savePauses({ volumeMs: 1, partMs: 2, chapterMs: 3, paragraphMs: 4, pauseMs: 5 });
    await audio.saveStep({ stepId: 'silence-trim', enabled: true, settings: { thresholdDb: -40 } });
    expect(await audio.testFfmpeg('C:\ffmpeg.exe')).toEqual({ success: true, message: 'ok' });

    expect(api.calls('PUT', '/api/settings/audio-processing')[0]?.body).toEqual({ werThreshold: 0.2 });
    expect(api.calls('PUT', '/api/settings/audio-processing/pauses')[0]?.body).toEqual({
      volumeMs: 1,
      partMs: 2,
      chapterMs: 3,
      paragraphMs: 4,
      pauseMs: 5,
    });
    expect(api.calls('PUT', '/api/settings/audio-processing/steps/silence-trim')[0]?.body).toEqual({
      stepId: 'silence-trim',
      enabled: true,
      settings: { thresholdDb: -40 },
    });
    expect(api.calls('POST', '/api/settings/audio-processing/ffmpeg/test')[0]?.body).toEqual({
      ffmpegPath: 'C:\ffmpeg.exe',
    });
  });

  it('AudioProcessingApi lists recent samples with the limit and previews one step', async () => {
    api.on('GET', '/api/audio/samples/recent', (_body, url) => [{ limit: url.searchParams.get('limit') }]);
    api.on('POST', '/api/settings/audio-processing/steps/consonant-soften/preview', { previewId: 'p1' });
    const audio = new AudioProcessingApi();

    expect(await audio.recentSamples()).toEqual([{ limit: '20' } as never]);
    const request = { sample: { folder: 'f', itemId: 'i' }, settings: { engine: 'adyneq' } };
    expect(await audio.previewStep('consonant-soften', request)).toEqual({ previewId: 'p1' } as never);
    expect(
      api.calls('POST', '/api/settings/audio-processing/steps/consonant-soften/preview')[0]?.body,
    ).toEqual(request);
  });
});
