import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import type {
  ParagraphTtsServiceConfig,
  ParagraphTtsServiceType,
  ProviderSettingsSchema,
  SettingsConfig,
  VoiceDesignServiceConfig,
  VoiceDesignServiceType,
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
 * subclass for the area you need; the base is generic over the entity. The LLM, transcription
 * and similarity subclasses (and the per-area test calls) arrive with the settings screens.
 */
export abstract class SettingsApi<TConfig extends SettingsConfig> {
  protected readonly api = use(ApiClient);
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

export class ParagraphTtsSettingsApi extends SettingsApi<ParagraphTtsServiceConfig> {
  constructor() {
    super('paragraph-tts');
  }

  /** The editable fields of one TTS provider type with ranges and recommended defaults (ticket 16). */
  schema(type: ParagraphTtsServiceType): Promise<ProviderSettingsSchema> {
    return this.api.get<ProviderSettingsSchema>('/api/settings/paragraph-tts/schema', { type });
  }
}

export class VoiceDesignSettingsApi extends SettingsApi<VoiceDesignServiceConfig> {
  constructor() {
    super('voice-design');
  }

  /** The editable fields of one voice-design provider type with ranges and recommended defaults (ticket 16). */
  schema(type: VoiceDesignServiceType): Promise<ProviderSettingsSchema> {
    return this.api.get<ProviderSettingsSchema>('/api/settings/voice-design/schema', { type });
  }
}
