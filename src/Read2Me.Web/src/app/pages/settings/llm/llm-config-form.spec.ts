import { AttributionPromptStyle, LlmApiType, LlmServerConfig } from '@app/api';
import {
  EMPTY_LLM_FORM,
  LlmConfigForm,
  buildLlmConfig,
  toLlmForm,
  validateLlmForm,
} from './llm-config-form';

const valid: LlmConfigForm = { ...EMPTY_LLM_FORM, name: 'Local', baseUrl: 'http://localhost:8080' };

describe('LLM config form', () => {
  it('reports the same first validation message as Blazor, in the same order', () => {
    expect(validateLlmForm({ ...valid, name: '  ' })).toBe('Name is required.');
    expect(validateLlmForm({ ...valid, baseUrl: '' })).toBe('Base URL is required.');
    expect(validateLlmForm({ ...valid, baseUrl: 'localhost:8080/v1 x' })).toBe(
      'Base URL must be a valid absolute URL (e.g. http://localhost:8080).',
    );
    expect(validateLlmForm({ ...valid, baseUrl: '/relative' })).toBe(
      'Base URL must be a valid absolute URL (e.g. http://localhost:8080).',
    );
    expect(validateLlmForm({ ...valid, temperature: 'warm' })).toBe(
      'Temperature must be a number.',
    );
    expect(validateLlmForm({ ...valid, topP: '0,9' })).toBe('Top P must be a number.');
    expect(validateLlmForm({ ...valid, maxTokens: '12.5' })).toBe(
      'Max tokens must be a whole number.',
    );
    expect(validateLlmForm({ ...valid, maxTokens: '99999999999' })).toBe(
      'Max tokens must be a whole number.',
    );
    expect(validateLlmForm({ ...valid, frequencyPenalty: 'x' })).toBe(
      'Frequency penalty must be a number.',
    );
    expect(validateLlmForm({ ...valid, presencePenalty: 'x' })).toBe(
      'Presence penalty must be a number.',
    );
    expect(validateLlmForm({ ...valid, attributionBatchSize: '0' })).toBe(
      'Paragraphs per request must be a whole number of 1 or more.',
    );
    expect(validateLlmForm({ ...valid, attributionBatchSize: 'two' })).toBe(
      'Paragraphs per request must be a whole number of 1 or more.',
    );
    expect(validateLlmForm({ ...valid, name: '', temperature: 'warm' })).toBe('Name is required.');
    expect(validateLlmForm(valid)).toBeNull();
  });

  it('accepts blank request parameters and a blank batch size', () => {
    expect(validateLlmForm({ ...valid, attributionBatchSize: ' ', temperature: '' })).toBeNull();
    expect(
      validateLlmForm({ ...valid, temperature: '-0.5', topP: '1e-1', maxTokens: '2048' }),
    ).toBeNull();
  });

  it('builds a config: trims, blanks become null (server default) and batch size defaults to 1', () => {
    const config = buildLlmConfig(
      {
        ...valid,
        name: '  Local  ',
        baseUrl: ' http://localhost:8080 ',
        apiKey: '  ',
        model: ' gemma-4b ',
        temperature: '0.7',
        maxTokens: '512',
        attributionBatchSize: '',
        promptStyle: AttributionPromptStyle.Simple,
        supportsModelSwitch: true,
      },
      9,
    );

    expect(config).toEqual<LlmServerConfig>({
      id: 9,
      name: 'Local',
      apiType: LlmApiType.OpenAiCompatible,
      baseUrl: 'http://localhost:8080',
      apiKey: null,
      model: 'gemma-4b',
      temperature: 0.7,
      topP: null,
      maxTokens: 512,
      frequencyPenalty: null,
      presencePenalty: null,
      attributionBatchSize: 1,
      promptStyle: AttributionPromptStyle.Simple,
      supportsModelSwitch: true,
    });
  });

  it('round-trips a config through the form', () => {
    const config: LlmServerConfig = {
      id: 3,
      name: 'Remote',
      apiType: LlmApiType.OpenAiCompatible,
      baseUrl: 'https://api.example.com/v1',
      apiKey: 'sk-1',
      model: null,
      temperature: 0,
      topP: 0.95,
      maxTokens: null,
      frequencyPenalty: null,
      presencePenalty: -1,
      attributionBatchSize: 4,
      promptStyle: AttributionPromptStyle.Full,
      supportsModelSwitch: false,
    };

    const form = toLlmForm(config);

    expect(form.temperature).toBe('0');
    expect(form.maxTokens).toBe('');
    expect(buildLlmConfig(form, config.id)).toEqual(config);
  });
});
