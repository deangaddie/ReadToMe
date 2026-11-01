import { use } from '@app/core/services';
import { ApiClient, type CallInit, type Operation, type SuccessBody } from './api-client';
import { ApiError } from './api-error';
import type {
  AttributionChainRequest,
  AttributionChainResponse,
  LlmServerConfig,
  LlmTestRequest,
  LlmTestStatusResponse,
  ParagraphTtsServiceType,
  ProviderSettingsSchema,
  SemanticSimilarityServiceType,
  SettingsConfig,
  SimilarityTestResponse,
  TextStepDto,
  TranscriptionServiceType,
  VoiceDesignSampleTextResponse,
  VoiceDesignServiceType,
  VoiceDesignTestResponse,
} from './dtos';

/** The five generic config areas `SettingsEndpoints.MapArea` serves. */
export type SettingsArea =
  | 'llm'
  | 'paragraph-tts'
  | 'voice-design'
  | 'transcription'
  | 'semantic-similarity';

/** The config entity area `A` serves, as the document types its active read. */
export type AreaConfig<A extends SettingsArea> = SuccessBody<
  Operation<`/api/settings/${A}/active`, 'get'>
>;

/** Every area's list / create route, and its by-id route. */
type AreaRoot = `/api/settings/${SettingsArea}`;
type AreaById = `${AreaRoot}/{id}`;

/**
 * One instance per area of the generic `/api/settings/{area}` surface: list / create / update /
 * delete plus the active selection. Bodies are the raw AppData entities. Use the concrete subclass
 * for the area you need: its methods are typed by its own area's routes ({@link AreaConfig}).
 *
 * The path-typed client can't resolve a route over a generic area, so the calls here go to the
 * union of the five areas' routes. The routes are checked against the document; the bodies and
 * answers are not (they are asserted as `A`'s entity, via {@link narrow}), so the public types
 * hold only because `root` is built from `area`. Calls on one concrete area belong in its subclass.
 */
export abstract class SettingsApi<A extends SettingsArea> {
  protected readonly api = use(ApiClient);

  protected constructor(readonly area: A) {}

  private get root(): AreaRoot {
    return `/api/settings/${this.area}`;
  }

  list(): Promise<AreaConfig<A>[]> {
    return narrow(this.api.get(this.root));
  }

  /** 201 with the stored row; the host assigns `id` and auto-activates the first config. */
  create(config: Omit<AreaConfig<A>, 'id'>): Promise<AreaConfig<A>> {
    const init = { body: { ...(config as object), id: 0 } } as CallInit<
      Operation<AreaRoot, 'post'>
    >;
    return narrow(this.api.post(this.root, init));
  }

  /** 404 when `config.id` does not exist. */
  update(config: AreaConfig<A>): Promise<AreaConfig<A>> {
    const { id } = config as SettingsConfig;
    const init = { path: { id }, body: config } as CallInit<Operation<AreaById, 'put'>>;
    return narrow(this.api.put(`${this.root}/{id}`, init));
  }

  /** The active selection reassigns or clears server-side. */
  delete(id: number): Promise<void> {
    return this.api.delete(`${this.root}/{id}`, { path: { id } });
  }

  /** The active config, or null when none is selected (the host answers 404). */
  async active(): Promise<AreaConfig<A> | null> {
    try {
      return await narrow<AreaConfig<A>>(this.api.get(`${this.root}/active`));
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  }

  /** 404 when `id` does not exist. */
  setActive(id: number): Promise<void> {
    return this.api.put(`${this.root}/active`, { body: { id } });
  }
}

/** Any area's answer, as area `A`'s: the routes it came from are `A`'s, by construction of `root`. */
function narrow<T>(answer: Promise<unknown>): Promise<T> {
  return answer as Promise<T>;
}

/** `LlmSettingsEndpoints.cs` on top of the generic area: models, the test console and the chain. */
export class LlmSettingsApi extends SettingsApi<'llm'> {
  constructor() {
    super('llm');
  }

  /** Model ids the server behind `config` offers; the config need not be saved. 422 carries the reason. */
  async models(config: LlmServerConfig): Promise<string[]> {
    const response = await this.api.post('/api/settings/llm/models', { body: config });
    return response.models;
  }

  /** 202; tokens arrive on `stream:llm` and the outcome as `llmTest` on `connectionId`. 409 while one runs. */
  startTest(id: number, request: LlmTestRequest): Promise<void> {
    return this.api.post('/api/settings/llm/{id}/test', { path: { id }, body: request });
  }

  cancelTest(id: number): Promise<void> {
    return this.api.post('/api/settings/llm/{id}/test/cancel', { path: { id } });
  }

  /** For a page that may have missed its `llmTest` message. */
  testStatus(): Promise<LlmTestStatusResponse> {
    return this.api.get('/api/settings/llm/test');
  }

  attributionChain(): Promise<AttributionChainResponse> {
    return this.api.get('/api/settings/llm/attribution-chain');
  }

  /** Replaces the chain and the self-consistency flag; 422 when a step names no config. */
  setAttributionChain(request: AttributionChainRequest): Promise<AttributionChainResponse> {
    return this.api.put('/api/settings/llm/attribution-chain', { body: request });
  }
}

export class ParagraphTtsSettingsApi extends SettingsApi<'paragraph-tts'> {
  constructor() {
    super('paragraph-tts');
  }

  /** The editable fields of one TTS provider type with ranges and recommended defaults (ticket 16). */
  schema(type: ParagraphTtsServiceType): Promise<ProviderSettingsSchema> {
    return this.api.get('/api/settings/paragraph-tts/schema', { query: { type } });
  }

  /** The built-in steps, then config `id`'s substitutions; id 0 lists the built-ins only. */
  textSteps(id: number): Promise<TextStepDto[]> {
    return this.api.get('/api/settings/paragraph-tts/{id}/text-steps', { path: { id } });
  }
}

export class VoiceDesignSettingsApi extends SettingsApi<'voice-design'> {
  constructor() {
    super('voice-design');
  }

  /** The editable fields of one voice-design provider type with ranges and recommended defaults (ticket 16). */
  schema(type: VoiceDesignServiceType): Promise<ProviderSettingsSchema> {
    return this.api.get('/api/settings/voice-design/schema', { query: { type } });
  }

  sampleText(): Promise<VoiceDesignSampleTextResponse> {
    return this.api.get('/api/settings/voice-design/sample-text');
  }

  /** Null, blank or the default text clears the override. */
  setSampleText(text: string | null): Promise<VoiceDesignSampleTextResponse> {
    return this.api.put('/api/settings/voice-design/sample-text', { body: { text } });
  }

  /** Designs a voice with the stored config `id` (up to 60 s); 422 carries the provider's reason. */
  test(id: number, prompt: string): Promise<VoiceDesignTestResponse> {
    return this.api.post('/api/settings/voice-design/{id}/test', {
      path: { id },
      body: { prompt },
    });
  }
}

export class TranscriptionSettingsApi extends SettingsApi<'transcription'> {
  constructor() {
    super('transcription');
  }

  schema(type: TranscriptionServiceType): Promise<ProviderSettingsSchema> {
    return this.api.get('/api/settings/transcription/schema', { query: { type } });
  }

  /** Transcribes a wav / mp3 / aac (≤ 50 MB) with the stored config `id`; 422 carries the provider's reason. */
  async test(id: number, audio: File): Promise<string> {
    const form = new FormData();
    form.append('file', audio, audio.name);
    const response = await this.api.postForm(
      '/api/settings/transcription/{id}/test',
      { path: { id } },
      form,
    );
    return response.transcript;
  }
}

export class SemanticSimilaritySettingsApi extends SettingsApi<'semantic-similarity'> {
  constructor() {
    super('semantic-similarity');
  }

  schema(type: SemanticSimilarityServiceType): Promise<ProviderSettingsSchema> {
    return this.api.get('/api/settings/semantic-similarity/schema', { query: { type } });
  }

  /** Scores two texts with the stored config `id`; 422 carries the provider's reason. */
  test(id: number, text1: string, text2: string): Promise<SimilarityTestResponse> {
    return this.api.post('/api/settings/semantic-similarity/{id}/test', {
      path: { id },
      body: { text1, text2 },
    });
  }
}
