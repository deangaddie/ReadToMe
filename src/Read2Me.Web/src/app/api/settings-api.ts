import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import type {
  AttributionChainRequest,
  AttributionChainResponse,
  LlmModelsResponse,
  LlmServerConfig,
  LlmTestRequest,
  LlmTestStatusResponse,
  ParagraphTtsServiceConfig,
  ParagraphTtsServiceType,
  ProviderSettingsSchema,
  SemanticSimilarityServiceConfig,
  SemanticSimilarityServiceType,
  SettingsConfig,
  SimilarityTestRequest,
  SimilarityTestResponse,
  TextStep,
  TranscriptionServiceConfig,
  TranscriptionServiceType,
  TranscriptionTestResponse,
  VoiceDesignSampleText,
  VoiceDesignSampleTextRequest,
  VoiceDesignServiceConfig,
  VoiceDesignServiceType,
  VoiceDesignTestRequest,
  VoiceDesignTestResponse,
} from './dtos';

/** The five generic config areas `SettingsEndpoints.MapArea` serves. */
export type SettingsArea =
  | 'llm'
  | 'paragraph-tts'
  | 'voice-design'
  | 'transcription'
  | 'semantic-similarity';

/**
 * One instance per area of the generic `/api/settings/{area}` surface: list / create / update /
 * delete plus the active selection. Bodies are the raw AppData entities. Use the concrete
 * subclass for the area you need; the base is generic over the entity. The transcription and
 * similarity subclasses (and their test calls) arrive with the provider settings screen.
 */
export abstract class SettingsApi<TConfig extends SettingsConfig> {
  protected readonly api = use(ApiClient);
  private readonly base: string;

  protected constructor(readonly area: SettingsArea) {
    this.base = `/api/settings/${area}`;
  }

  list(): Promise<TConfig[]> {
    return this.api.untypedGet<TConfig[]>(this.base);
  }

  /** 201 with the stored row; the host assigns `id` and auto-activates the first config. */
  create(config: Omit<TConfig, 'id'>): Promise<TConfig> {
    return this.api.untypedPost<TConfig>(this.base, { ...config, id: 0 });
  }

  /** 404 when `config.id` does not exist. */
  update(config: TConfig): Promise<TConfig> {
    return this.api.untypedPut<TConfig>(`${this.base}/${config.id}`, config);
  }

  /** The active selection reassigns or clears server-side. */
  delete(id: number): Promise<void> {
    return this.api.untypedDelete(`${this.base}/${id}`);
  }

  /** The active config, or null when none is selected (the host answers 404). */
  async active(): Promise<TConfig | null> {
    try {
      return await this.api.untypedGet<TConfig>(`${this.base}/active`);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  }

  /** 404 when `id` does not exist. */
  setActive(id: number): Promise<void> {
    return this.api.untypedPut<void>(`${this.base}/active`, { id });
  }
}

/** `LlmSettingsEndpoints.cs` on top of the generic area: models, the test console and the chain. */
export class LlmSettingsApi extends SettingsApi<LlmServerConfig> {
  constructor() {
    super('llm');
  }

  /** Model ids the server behind `config` offers; the config need not be saved. 422 carries the reason. */
  async models(config: LlmServerConfig): Promise<string[]> {
    const response = await this.api.untypedPost<LlmModelsResponse>(
      '/api/settings/llm/models',
      config,
    );
    return response.models;
  }

  /** 202; tokens arrive on `stream:llm` and the outcome as `llmTest` on `connectionId`. 409 while one runs. */
  startTest(id: number, request: LlmTestRequest): Promise<void> {
    return this.api.untypedPost<void>(`/api/settings/llm/${id}/test`, request);
  }

  cancelTest(id: number): Promise<void> {
    return this.api.untypedPost<void>(`/api/settings/llm/${id}/test/cancel`, null);
  }

  /** For a page that may have missed its `llmTest` message. */
  testStatus(): Promise<LlmTestStatusResponse> {
    return this.api.untypedGet<LlmTestStatusResponse>('/api/settings/llm/test');
  }

  attributionChain(): Promise<AttributionChainResponse> {
    return this.api.untypedGet<AttributionChainResponse>('/api/settings/llm/attribution-chain');
  }

  /** Replaces the chain and the self-consistency flag; 422 when a step names no config. */
  setAttributionChain(request: AttributionChainRequest): Promise<AttributionChainResponse> {
    return this.api.untypedPut<AttributionChainResponse>(
      '/api/settings/llm/attribution-chain',
      request,
    );
  }
}

export class ParagraphTtsSettingsApi extends SettingsApi<ParagraphTtsServiceConfig> {
  constructor() {
    super('paragraph-tts');
  }

  /** The editable fields of one TTS provider type with ranges and recommended defaults (ticket 16). */
  schema(type: ParagraphTtsServiceType): Promise<ProviderSettingsSchema> {
    return this.api.untypedGet<ProviderSettingsSchema>('/api/settings/paragraph-tts/schema', {
      type,
    });
  }

  /** The built-in steps, then config `id`'s substitutions; id 0 lists the built-ins only. */
  textSteps(id: number): Promise<TextStep[]> {
    return this.api.untypedGet<TextStep[]>(`/api/settings/paragraph-tts/${id}/text-steps`);
  }
}

export class VoiceDesignSettingsApi extends SettingsApi<VoiceDesignServiceConfig> {
  constructor() {
    super('voice-design');
  }

  /** The editable fields of one voice-design provider type with ranges and recommended defaults (ticket 16). */
  schema(type: VoiceDesignServiceType): Promise<ProviderSettingsSchema> {
    return this.api.untypedGet<ProviderSettingsSchema>('/api/settings/voice-design/schema', {
      type,
    });
  }

  sampleText(): Promise<VoiceDesignSampleText> {
    return this.api.untypedGet<VoiceDesignSampleText>('/api/settings/voice-design/sample-text');
  }

  /** Null, blank or the default text clears the override. */
  setSampleText(text: string | null): Promise<VoiceDesignSampleText> {
    const request: VoiceDesignSampleTextRequest = { text };
    return this.api.untypedPut<VoiceDesignSampleText>(
      '/api/settings/voice-design/sample-text',
      request,
    );
  }

  /** Designs a voice with the stored config `id` (up to 60 s); 422 carries the provider's reason. */
  test(id: number, prompt: string): Promise<VoiceDesignTestResponse> {
    const request: VoiceDesignTestRequest = { prompt };
    return this.api.untypedPost<VoiceDesignTestResponse>(
      `/api/settings/voice-design/${id}/test`,
      request,
    );
  }
}

export class TranscriptionSettingsApi extends SettingsApi<TranscriptionServiceConfig> {
  constructor() {
    super('transcription');
  }

  schema(type: TranscriptionServiceType): Promise<ProviderSettingsSchema> {
    return this.api.untypedGet<ProviderSettingsSchema>('/api/settings/transcription/schema', {
      type,
    });
  }

  /** Transcribes a wav / mp3 / aac (≤ 50 MB) with the stored config `id`; 422 carries the provider's reason. */
  async test(id: number, audio: File): Promise<string> {
    const form = new FormData();
    form.append('file', audio, audio.name);
    const response = await this.api.untypedPostForm<TranscriptionTestResponse>(
      `/api/settings/transcription/${id}/test`,
      form,
    );
    return response.transcript;
  }
}

export class SemanticSimilaritySettingsApi extends SettingsApi<SemanticSimilarityServiceConfig> {
  constructor() {
    super('semantic-similarity');
  }

  schema(type: SemanticSimilarityServiceType): Promise<ProviderSettingsSchema> {
    return this.api.untypedGet<ProviderSettingsSchema>('/api/settings/semantic-similarity/schema', {
      type,
    });
  }

  /** Scores two texts with the stored config `id`; 422 carries the provider's reason. */
  test(id: number, text1: string, text2: string): Promise<SimilarityTestResponse> {
    const request: SimilarityTestRequest = { text1, text2 };
    return this.api.untypedPost<SimilarityTestResponse>(
      `/api/settings/semantic-similarity/${id}/test`,
      request,
    );
  }
}
