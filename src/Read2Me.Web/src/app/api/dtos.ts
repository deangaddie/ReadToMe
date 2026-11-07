/**
 * The hand-written half of the Agent API's wire shapes.
 *
 * Every shape the host's OpenAPI document declares comes generated: openapi-typescript emits it into
 * `schema.d.ts` under its C# name (ADR 0017), with the record's XML doc as its JSDoc, and `index.ts`
 * re-exports them all. Don't alias one here; a second export of a generated name fails typecheck.
 *
 * What stays here is what the document doesn't describe: multipart form fields, a shape the web app
 * itself abstracts over (`SettingsConfig`, `ProviderType`), the stored step settings the host keeps
 * as raw JSON, the member names of a value the host reads as text (a route segment such as
 * `AiTaskKind`), which the document types `string`, and the two refinements that narrow such a
 * field on a generated shape (`PromptCatalogEntry`, `TryPronunciationRequest`).
 *
 * Conventions: camelCase property names, `Guid` as string, and every enum as its PascalCase member
 * name, in bodies, route segments and dictionary keys alike (#166). A feature that needs an enum's
 * labels or order keeps a `Record<Union, string>` or a `readonly Union[]` beside its own code.
 */
import type {
  ApiItemType,
  AudioReviewDto,
  components,
  ItemVoiceDto,
  ParagraphTtsServiceType,
  ParagraphTtsSettingsSchema,
  paths,
  SemanticSimilarityServiceType,
  SemanticSimilaritySettingsSchema,
  TranscriptionServiceType,
  TranscriptionSettingsSchema,
  VoiceDesignServiceType,
  VoiceDesignSettingsSchema,
} from './schema';

/** Component schemas the host's OpenAPI document declares: request bodies and typed responses. */
export type Schema = components['schemas'];

/** A `System.Guid` on the wire. */
export type Guid = string;

// ---- Settings areas -------------------------------------------------------------------------------

/** Any provider settings area's type: what the shared provider page, store and editor carry. */
export type ProviderType =
  | ParagraphTtsServiceType
  | VoiceDesignServiceType
  | TranscriptionServiceType
  | SemanticSimilarityServiceType;

/** Any provider area's settings schema: what the shared provider page, store and editor carry. */
export type ProviderSettingsSchema =
  | ParagraphTtsSettingsSchema
  | VoiceDesignSettingsSchema
  | TranscriptionSettingsSchema
  | SemanticSimilaritySettingsSchema;

/** What every config entity the generic settings areas serve has in common; routes key on `id`. */
export interface SettingsConfig {
  id: number;
  name: string;
}

// ---- Projects (`ProjectEndpoints.cs`) -------------------------------------------------------------

/**
 * `PipelineStep`: the nine stepper step ids, which are also the checkpoint route segment and the
 * keys of `ProjectStatusDto.checkpoints`.
 */
export const PIPELINE_STEP_IDS = [
  'Import',
  'Structure',
  'Speakers',
  'Text',
  'Voices',
  'Titles',
  'Pauses',
  'Audio',
  'Export',
] as const;
export type PipelineStepId = (typeof PIPELINE_STEP_IDS)[number];

/** Multipart fields for `POST /api/projects`; `title` and `file` are required by the host. */
export interface CreateProjectRequest {
  title: string;
  bookTitle?: string;
  author?: string;
  file: File;
}

/**
 * `SplitRuleRequest.mode`'s names: how a level's headings are recognised (`ManualImportRequest.cs`).
 * The host reads the mode as text, case-insensitively, so the schema types it `string`.
 */
export type SplitRuleMode = 'Prefix' | 'Arabic' | 'Roman';

// ---- Book reads (`BookEndpoints.cs`) ---------------------------------------------------------------

export type PauseKind = Exclude<ApiItemType, 'Narration' | 'Character'>;

/** `GET …/nodes/Chapter/{id}/voices`: keyed by speech item id. */
export type ChapterVoicesDto = Record<Guid, ItemVoiceDto>;

/** `GET …/audio/reviews`: sparse, keyed by item id. */
export type AudioReviewsDto = Record<Guid, AudioReviewDto>;

// ---- Voices (`VoiceEndpoints.cs`) ------------------------------------------------------------------

/**
 * Optional `generate-audio` body: an unsaved prompt to design from, stored with the take. The
 * handler reads it by hand, so the document has it inline on its operation, not as a component.
 */
export type GenerateVoiceAudioRequest = NonNullable<
  paths['/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio']['post']['requestBody']
>['content']['application/json'];

// ---- Prompts (`SettingsEndpoints.cs`) --------------------------------------------------------------

export const PROMPT_KINDS = [
  'character',
  'simple-character',
  'batch-character',
  'simple-batch-character',
  'voice',
  'voice-plan',
  'narrator-voice-plan',
  'discover-characters',
] as const;
export type PromptKind = (typeof PROMPT_KINDS)[number];

/**
 * `GET /api/settings/prompts/catalog` entry, with `kind` narrowed: the host sends it as a string
 * (kinds are kebab-case route segments, not enum members), but it is always one of
 * {@link PROMPT_KINDS}.
 */
export type PromptCatalogEntry = Omit<Schema['PromptCatalogEntry'], 'kind'> & { kind: PromptKind };

// ---- Audio processing page (`AudioProcessingEndpoints.cs`, ticket 24) ------------------------------

/** The two paragraph post-process steps, in pipeline order. */
export const AUDIO_STEP_IDS = {
  silenceTrim: 'silence-trim',
  consonantSoften: 'consonant-soften',
} as const;

/*
 * A step's stored settings payload (`AudioPostProcessStepConfig.settings`): `SilenceTrimSettings` or
 * `ConsonantSoftenSettings`. The host stores it as raw JSON, so the document leaves it unknown and
 * the step's form narrows it to one of these.
 */

export interface SilenceTrimSettings {
  thresholdDb: number;
  padMs: number;
  /** Fixed server-side; carried through so a save round-trips the stored value. */
  minOutputMs?: number;
}

export type ConsonantSoftenEngine = 'adyneq' | 'deesser';
export type ConsonantSoftenPreset = 'light' | 'medium' | 'strong' | 'custom';

/** Full adynEQ parameter set; threshold in dB (the host converts to linear amplitude). */
export interface AdynEqParams {
  thresholdDb: number;
  ratio: number;
  rangeDb: number;
  detectFrequencyHz: number;
  detectQ: number;
  targetFrequencyHz: number;
  targetQ: number;
  attackMs: number;
  releaseMs: number;
  shelfFrequencyHz: number;
  shelfGainDb: number;
  /** Optional 1-pole highpass cutoff; presets never set it. */
  highpassHz?: number;
}

export interface DeesserParams {
  intensity: number;
  makeupAmount: number;
  frequency: number;
  shelfFrequencyHz: number;
  shelfGainDb: number;
  highpassHz?: number;
}

/** A preset reference: raw params are present only when `preset` is `custom`. */
export interface ConsonantSoftenSettings {
  engine: ConsonantSoftenEngine;
  preset: ConsonantSoftenPreset;
  adynEq?: AdynEqParams | null;
  deesser?: DeesserParams | null;
}

// ---- Preflight (`PreflightEndpoints.cs`, ticket 25) -------------------------------------------------

/** `AiTaskKind` member names — the route segment of `/api/preflight/{taskKind}`. */
export type AiTaskKind =
  | 'CharacterAttribution'
  | 'AudioGeneration'
  | 'VoicePromptGeneration'
  | 'CharacterDiscovery'
  | 'VoiceDesignAudio'
  | 'Transcription'
  | 'BookEdit';

// ---- Pronunciations (`PronunciationEndpoints.cs`) ---------------------------------------------------

/** A try-out's voice: one of the project's voices, or the item's own resolved voice. */
export type TryVoice = { voiceId: Guid } | 'item';

/**
 * `POST …/pronunciations/try`, with `voice` narrowed: the host reads it as a raw JSON element, so the
 * client narrows it to {@link TryVoice}.
 */
export type TryPronunciationRequest = Omit<Schema['TryPronunciationRequest'], 'voice'> & {
  voice: TryVoice;
};
