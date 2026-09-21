import { Injectable, inject } from '@angular/core';
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
  VoiceDesignServiceType,
  SemanticSimilarityServiceConfig,
  SettingsConfig,
  TranscriptionServiceConfig,
  VoiceDesignServiceConfig,
} from './dtos';

/** The five generic config areas `SettingsEndpoints.MapArea` serves. */
export type SettingsArea =
  'llm' | 'paragraph-tts' | 'voice-design' | 'transcription' | 'semantic-similarity';

/**
 * One instance per area of the generic `/api/settings/{area}` surface: list / create / update /
 * delete plus the active selection. Bodies are the raw AppData entities. Inject the concrete
 * subclass for the area you need (`LlmSettingsApi` etc.); the base is generic over the entity.
 */
export abstract class SettingsApi<TConfig extends SettingsConfig> {
  private readonly api = inject(ApiClient);
  private readonly base: string;

  protected constructor(readonly area: SettingsArea) {
    this.base = `/api/settings/${area}`;
  }

  list(): Promise<TConfig[]> {
    return this.api.get<TConfig[]>(this.base);
  }

  /** 201 with the stored row; the host assigns `id` and auto-activates the first config. */
  create(config: Omit<TConfig, 'id'>): Promise<TConfig> {
    return this.api.post<TConfig>(this.base, { ...config, id: 0 });
  }

  /** 404 when `config.id` does not exist. */
  update(config: TConfig): Promise<TConfig> {
    return this.api.put<TConfig>(`${this.base}/${config.id}`, config);
  }

  /** The active selection reassigns or clears server-side. */
  delete(id: number): Promise<void> {
    return this.api.delete(`${this.base}/${id}`);
  }

  /** The active config, or null when none is selected (the host answers 404). */
  async active(): Promise<TConfig | null> {
    try {
      return await this.api.get<TConfig>(`${this.base}/active`);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  }

  /** 404 when `id` does not exist. */
  setActive(id: number): Promise<void> {
    return this.api.put<void>(`${this.base}/active`, { id });
  }
}

@Injectable({ providedIn: 'root' })
export class LlmSettingsApi extends SettingsApi<LlmServerConfig> {
  private readonly client = inject(ApiClient);

  constructor() {
    super('llm');
  }

  /** Model ids the server behind `config` offers; the config need not be saved. 422 carries the reason. */
  async models(config: LlmServerConfig): Promise<string[]> {
    const response = await this.client.post<LlmModelsResponse>('/api/settings/llm/models', config);
    return response.models;
  }

  /** 202; tokens arrive on `stream:llm` and the outcome as `llmTest` on `connectionId`. 409 while one runs. */
  startTest(id: number, request: LlmTestRequest): Promise<void> {
    return this.client.post<void>(`/api/settings/llm/${id}/test`, request);
  }

  cancelTest(id: number): Promise<void> {
    return this.client.post<void>(`/api/settings/llm/${id}/test/cancel`, null);
  }

  /** For a page that may have missed its `llmTest` message. */
  testStatus(): Promise<LlmTestStatusResponse> {
    return this.client.get<LlmTestStatusResponse>('/api/settings/llm/test');
  }

  attributionChain(): Promise<AttributionChainResponse> {
    return this.client.get<AttributionChainResponse>('/api/settings/llm/attribution-chain');
  }

  /** Replaces the chain and the self-consistency flag; 422 when a step names no config. */
  setAttributionChain(request: AttributionChainRequest): Promise<AttributionChainResponse> {
    return this.client.put<AttributionChainResponse>(
      '/api/settings/llm/attribution-chain',
      request,
    );
  }
}

@Injectable({ providedIn: 'root' })
export class ParagraphTtsSettingsApi extends SettingsApi<ParagraphTtsServiceConfig> {
  private readonly client = inject(ApiClient);

  constructor() {
    super('paragraph-tts');
  }

  /** The editable fields of one TTS provider type with ranges and recommended defaults (ticket 16). */
  schema(type: ParagraphTtsServiceType): Promise<ProviderSettingsSchema> {
    return this.client.get<ProviderSettingsSchema>('/api/settings/paragraph-tts/schema', { type });
  }
}

@Injectable({ providedIn: 'root' })
export class VoiceDesignSettingsApi extends SettingsApi<VoiceDesignServiceConfig> {
  private readonly client = inject(ApiClient);

  constructor() {
    super('voice-design');
  }

  /** The editable fields of one voice-design provider type with ranges and recommended defaults (ticket 16). */
  schema(type: VoiceDesignServiceType): Promise<ProviderSettingsSchema> {
    return this.client.get<ProviderSettingsSchema>('/api/settings/voice-design/schema', { type });
  }
}

@Injectable({ providedIn: 'root' })
export class TranscriptionSettingsApi extends SettingsApi<TranscriptionServiceConfig> {
  constructor() {
    super('transcription');
  }
}

@Injectable({ providedIn: 'root' })
export class SemanticSimilaritySettingsApi extends SettingsApi<SemanticSimilarityServiceConfig> {
  constructor() {
    super('semantic-similarity');
  }
}
