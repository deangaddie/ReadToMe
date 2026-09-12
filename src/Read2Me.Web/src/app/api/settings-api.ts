import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import type {
  LlmServerConfig,
  ParagraphTtsServiceConfig,
  SemanticSimilarityServiceConfig,
  SettingsConfig,
  TranscriptionServiceConfig,
  VoiceDesignServiceConfig,
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
  constructor() {
    super('llm');
  }
}

@Injectable({ providedIn: 'root' })
export class ParagraphTtsSettingsApi extends SettingsApi<ParagraphTtsServiceConfig> {
  constructor() {
    super('paragraph-tts');
  }
}

@Injectable({ providedIn: 'root' })
export class VoiceDesignSettingsApi extends SettingsApi<VoiceDesignServiceConfig> {
  constructor() {
    super('voice-design');
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
