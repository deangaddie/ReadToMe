/**
 * Wire shapes for the Agent API (spec D8, ticket 05).
 *
 * A type the host's OpenAPI document declares is an alias, `export type X = Schema['X']`, named as
 * its C# record (ADR 0017). Endpoints not yet typed (their slice of #171 hasn't landed) answer
 * untyped, so their response DTOs are still hand-written here, mirrored from the C# records in
 * `src/Read2Me.App/Api/*.cs`, and any that exist in the schema are pinned to it in `schema-pins.ts`.
 *
 * Conventions: camelCase property names, `Guid` as string, and every enum as its PascalCase member
 * name, in bodies, route segments and dictionary keys alike (#166). A feature that needs an enum's
 * labels or order keeps a `Record<Union, string>` or a `readonly Union[]` beside its own code.
 */
import type { components, paths } from './schema';
import type { BookEditTargetKind, NodeKind, Placement, VoiceAnchorLevel } from './book-commands';

/** Component schemas the host's OpenAPI document declares (request bodies + settings entities). */
export type Schema = components['schemas'];

/** A `System.Guid` on the wire. */
export type Guid = string;

/** `BookNodeLevel`: the route segment of node-scoped endpoints (`/nodes/{level}/…`) and enqueue requests. */
export type NodeLevel = Schema['BookNodeLevel'];

// ---- Settings enums -------------------------------------------------------------------------------

export type LlmApiType = Schema['LlmApiType'];
export type AttributionPromptStyle = Schema['AttributionPromptStyle'];
export type ParagraphTtsServiceType = Schema['ParagraphTtsServiceType'];
export type VoiceDesignServiceType = Schema['VoiceDesignServiceType'];
export type TranscriptionServiceType = Schema['TranscriptionServiceType'];
export type SemanticSimilarityServiceType = Schema['SemanticSimilarityServiceType'];

/** Any provider settings area's type: what the shared provider page, store and editor carry. */
export type ProviderType =
  | ParagraphTtsServiceType
  | VoiceDesignServiceType
  | TranscriptionServiceType
  | SemanticSimilarityServiceType;

/** `ParagraphOutcomeKind` / `AudioItemOutcomeKind`: same members. */
export type OutcomeKind = 'Failed' | 'Unfinished';

// ---- Projects (`ProjectEndpoints.cs`) -------------------------------------------------------------

/** One row of `GET /api/projects`; `fileType` is null for a folder whose DB could not be opened. */
export type ProjectSummary = Schema['ProjectSummary'];

/** `PATCH /api/projects/{folder}`: every field optional, omitted/null fields are left as they are. */
export type UpdateProjectRequest = Schema['UpdateProjectRequest'];

export type NarratorOnlyModeRequest = Schema['NarratorOnlyModeRequest'];

/**
 * `PipelineStep`: the nine stepper step ids, which are also the checkpoint route segment and the
 * keys of {@link ProjectStatusDto.checkpoints}.
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

/** `PUT /api/projects/{folder}/checkpoints/{step}`: `Open` deletes the row. */
export type SetCheckpointRequest = Schema['SetCheckpointRequest'];
export type CheckpointRequestState = NonNullable<SetCheckpointRequest['state']>;

export type CoverImageResponse = Schema['CoverImageResponse'];

/** Multipart fields for `POST /api/projects`; `title` and `file` are required by the host. */
export interface CreateProjectRequest {
  title: string;
  bookTitle?: string;
  author?: string;
  file: File;
}

export type CreateProjectResponse = Schema['CreateProjectResponse'];

/** Who narrates the book; an unlinked narrator is the seed row ("Narrator", `isLinked` false). */
export type NarratorDto = Schema['NarratorDto'];

export type ProjectDetailDto = Schema['ProjectDetailDto'];

export type ImportRequest = Schema['ImportRequest'];

/**
 * `SplitRuleRequest.mode`'s names: how a level's headings are recognised (`ManualImportRequest.cs`).
 * The host reads the mode as text, case-insensitively, so the schema types it `string`.
 */
export type SplitRuleMode = 'Prefix' | 'Arabic' | 'Roman';

/** One level's rule; `prefix` is required (non-blank) when `mode` is `Prefix`, ignored otherwise. */
export type SplitRuleRequest = Schema['SplitRuleRequest'];

/**
 * `POST /api/projects/{folder}/import/manual`: `volume` is read only when `hasMultipleVolumes`,
 * `part` only when `hasMultipleParts`; `chapter` always. 400 when a switched-on level lacks a rule.
 */
export type ManualImportRequest = Schema['ManualImportRequest'];

// ---- Project roll-ups (`ProjectStatusEndpoints.cs`) -----------------------------------------------

/** `StepCheckpointDto`: a confirmed or skipped step; `at` is when, kept when the row goes stale. */
export type StepCheckpointDto = Schema['StepCheckpointDto'];

/** Paragraph counts under one volume/part/chapter. */
export type NodeStatusSummaryDto = Schema['NodeStatusSummaryDto'];

/**
 * `GET /api/projects/{folder}/status`. Item counts are items; `attribution`, `audio` and `review`
 * count paragraphs, like the node summaries. `revision` is the book revision the reads started at;
 * `volumeIds` are in book order; `checkpoints` holds only the steps that have a row, keyed by step id.
 */
export type ProjectStatusDto = Schema['ProjectStatusDto'];

// ---- Book reads (`BookEndpoints.cs`) ---------------------------------------------------------------

export interface NodeDto {
  id: Guid;
  title: string | null;
}

export type PauseKind = 'Pause' | 'ParagraphPause' | 'ChapterPause' | 'PartPause' | 'VolumePause';

/** `ApiItemType`: narration when the speaker is the narrator, dialog otherwise. */
export type ParagraphItemType = 'Narration' | 'Character' | PauseKind;

export interface ParagraphItemDto {
  id: Guid;
  /** Kept for older readers; new code reads {@link isPause} and the speaker. */
  itemType: ParagraphItemType;
  text: string | null;
  characterId: Guid | null;
  audioFileName: string | null;
  voiceInstructions: string | null;
  /** Fractional position key within the paragraph; items already arrive in this order. */
  orderKey: string;
  isPause: boolean;
}

export interface ParagraphDto {
  id: Guid;
  items: ParagraphItemDto[];
  /** No items, or a single pause item. */
  isPauseParagraph: boolean;
}

/** `GET …/nodes/Chapter/{id}/voices` entry. */
export interface ItemVoiceDto {
  /** null when no voice resolves for the item. */
  voiceName: string | null;
  /** The linked narrator's name on a narration item. */
  narratedBy: string | null;
}

/** Keyed by speech item id. */
export type ChapterVoicesDto = Record<Guid, ItemVoiceDto>;

export type AudioReviewStateName = 'NeedsReview' | 'Dismissed';

/** `AudioEndpoints.AudioReviewDto`: the hub's `AudioReviewInfo` with nulls sent explicitly. */
export interface AudioReviewDto {
  state: AudioReviewStateName;
  normalizeOk: boolean;
  normalizeReason: string | null;
  verifyOk: boolean;
  wer: number | null;
  verifyReason: string | null;
  transcript: string | null;
  originalTextSnapshot: string | null;
}

/** `GET …/audio/reviews`: sparse, keyed by item id. */
export type AudioReviewsDto = Record<Guid, AudioReviewDto>;

/** Exactly one of the three lists is present, chosen by the requested level. */
export interface NodeChildrenDto {
  parts: NodeDto[] | null;
  chapters: NodeDto[] | null;
  paragraphs: ParagraphDto[] | null;
}

/** `GET …/nodes/{level}/{id}/paragraph-ids` entry: a Character paragraph and the nodes it rolls up into. */
export interface ParagraphRefDto {
  id: Guid;
  chapterId: Guid;
  partId: Guid;
  volumeId: Guid;
}

/** `GET …/nodes/{level}/{id}/item-ids` entry: a speech item with a speaker and the nodes it rolls up into. */
export interface AudioItemRefDto {
  id: Guid;
  paragraphId: Guid;
  chapterId: Guid;
  partId: Guid;
  volumeId: Guid;
}

/** `POST …/characters/bulk-assign-preview` body. */
export interface BulkAssignPreviewRequest {
  paragraphIds: Guid[];
}

/** What a bulk speaker assign would write; paragraphs without dialog count in neither. */
export interface BulkAssignPreviewDto {
  paragraphsWithCharacterItems: number;
  characterItems: number;
}

/** Where a Release narration reaches: no whole-book scope (`NarrationReleaseScope`). */
export type NarrationReleaseScope = 'Part' | 'Chapter' | 'Paragraph';

/** `POST …/narration/release-preview` body. */
export interface NarrationReleasePreviewRequest {
  scope: NarrationReleaseScope;
  nodeId: Guid;
}

/**
 * What a ReleaseNarration would write: paragraphs it releases, items whose audio it discards, and
 * for a Chapter or Part the speech markers that passed (count descending, then marker; empty for a
 * Paragraph and when nothing passes).
 */
export interface NarrationReleasePreviewDto {
  paragraphs: number;
  itemsWithAudio: number;
  speechMarkers: SpeechMarkerCountDto[];
}

/** A speech marker as written (`Q.`, `Darcy:`, `—…—`) and how many paragraphs it releases. */
export interface SpeechMarkerCountDto {
  marker: string;
  paragraphs: number;
}

export interface CharacterAliasDto {
  id: Guid;
  name: string;
}

export interface CharacterDto {
  id: Guid;
  name: string;
  aliases: CharacterAliasDto[];
}

export interface BookOverviewDto {
  hasContent: boolean;
  volumes: NodeDto[];
  characters: CharacterDto[];
  totalParts: number;
  totalChapters: number;
  /** The book revision the read ran against: the reader's baseline for receipts and snapshots. */
  revision: number;
  /** The Structure rules, one entry per anchor kind: what the node menu renders its inserts from. */
  structureRules: StructureRuleDto[];
}

/** What a Structure rules entry is keyed by; a pause paragraph is a `Paragraph` to the commands. */
export type StructureAnchor =
  | 'Volume'
  | 'Part'
  | 'Chapter'
  | 'Paragraph'
  | 'PauseParagraph'
  | 'ParagraphItem';

/** One placement an anchor allows: the kind it creates, and whether that node needs text. */
export interface StructurePlacementDto {
  placement: Placement;
  creates: NodeKind;
  textRequired: boolean;
}

/** One Structure rules entry; `nodeKind` is what a command names the anchor as. */
export interface StructureRuleDto {
  anchor: StructureAnchor;
  nodeKind: NodeKind;
  placements: StructurePlacementDto[];
  /** How many of this kind the book always keeps (Volume: 1). */
  minimumCount: number;
}

// ---- Commands (`CommandEndpoints.cs`) --------------------------------------------------------------

export interface CommandResponse {
  newEntityId: Guid | null;
}

// ---- Attribution + audio queues (`AttributionEndpoints.cs`, `AudioEndpoints.cs`, `QueueStatusEndpoints.cs`)

export type NodeEnqueueRequest = Schema['NodeEnqueueRequest'];
export type AudioEnqueueRequest = Schema['AudioEnqueueRequest'];

/** `POST …/attribution/enqueue-paragraphs` body: an explicit selection; ids without dialog are ignored. */
export interface ParagraphsEnqueueRequest {
  paragraphIds: Guid[];
}

/** `POST …/audio/enqueue-items` body: an explicit item selection, or one item to retry; unknown ids are ignored. */
export interface ItemsEnqueueRequest {
  itemIds: Guid[];
}

export interface EnqueueResponse {
  enqueued: number;
}

/** `ParagraphQueueStatus` / `AudioItemQueueStatus` member names; null when not in the queue. */
export type QueueItemStatus = 'Queued' | 'Processing';

export interface QueueOutcome {
  kind: OutcomeKind;
  reason: string | null;
}

export interface ParagraphAttributionStatusDto {
  status: QueueItemStatus | null;
  outcome: QueueOutcome | null;
}

export interface AudioItemStatusDto {
  status: QueueItemStatus | null;
  outcome: QueueOutcome | null;
  audioVersion: number | null;
}

/** `GET /api/attribution/queue`. */
export type AttributionQueueSnapshot = Schema['AttributionQueueSnapshot'];

/** `GET /api/audio/queue`. */
export type AudioQueueSnapshot = Schema['AudioQueueSnapshot'];

// ---- Character discovery (`DiscoveryEndpoints.cs`) -------------------------------------------------

/** `DiscoveryStatus` minus `NoLlmConfigured`, which the endpoint turns into a 422. */
export type DiscoveryStatus = 'Ok' | 'Failed' | 'ServiceUnavailable';

export interface DiscoveredCharacterDto {
  name: string;
  aliases: string[];
  /** The roster character the row resolves onto by name or alias — the "Already exists" row. */
  existingCharacterId: Guid | null;
}

export interface DiscoveryOutcomeDto {
  status: DiscoveryStatus;
  reason: string | null;
  characters: DiscoveredCharacterDto[];
  /** Names two characters would own once every row is applied; advisory, recomputed client-side as rows change. */
  collisions: string[];
}

export type ApplyDiscoveryRow = Schema['ApplyDiscoveryRow'];

export interface ApplyDiscoveryResponse {
  applied: number;
}

// ---- Voices (`VoiceEndpoints.cs`) ------------------------------------------------------------------

export type VoiceSource = Schema['VoiceSource'];

/**
 * A voice as the cast page shows it. `isEdited` is the host's on-disk invariant: the audio has
 * been through the voice editor, so fresh audio would discard that edit. The override JSONs are
 * sparse patches keyed as the provider's settings schema lists them (ticket 16).
 * `referenceSeconds` is the stored audio's length (null without audio); `referenceWarning` is set
 * over the Reference Limit's 15 s soft limit (kept, but warned).
 */
export type VoiceDto = Schema['VoiceDto'];

export type TranscribeVoiceResponse = Schema['TranscribeVoiceResponse'];

/**
 * One voice rule as `GET …/characters/{id}/voice-rules` lists it (ticket 17), in evaluation
 * order: the default rule first, then by `order` (the fractional rank; the last passing rule
 * wins). Levels are `VoiceAnchorLevel` names, null on the default rule and on an open "from here
 * on" end. A dangling anchor names a node that no longer exists, so its title is null.
 */
export interface VoiceRuleDto {
  ruleId: Guid;
  voiceId: Guid;
  voiceName: string;
  isDefault: boolean;
  fromLevel: VoiceAnchorLevel | null;
  fromNodeId: Guid | null;
  fromTitle: string | null;
  fromDangling: boolean;
  toLevel: VoiceAnchorLevel | null;
  toNodeId: Guid | null;
  toTitle: string | null;
  toDangling: boolean;
  order: string;
}

/** `GET …/characters/{id}/voice-rules/preview` row: the voice the rules pick at the chapter's start (null = none). */
export interface ChapterVoicePreviewDto {
  chapterId: Guid;
  chapterTitle: string;
  voiceName: string | null;
}

export type RenderedDesignPromptResponse = Schema['RenderedDesignPromptResponse'];
export type GenerateDesignPromptRequest = Schema['GenerateDesignPromptRequest'];
export type GenerateDesignPromptResponse = Schema['GenerateDesignPromptResponse'];

// ---- Provider settings schemas (`ProviderSettingsSchema.cs`) ----------------------------------------

/** One choice of an `enum` field. */
export type SettingsOptionDto = Schema['SettingsOptionDto'];

/**
 * One editable field of a provider's settings; `key` is the settingsJson property name. `kind` is
 * one of `number` | `boolean` | `enum` | `string` | `text` (a string on the host, not an enum); a
 * `nullable` number may be left blank, meaning "use the server's default".
 */
export type SettingsFieldDto = Schema['SettingsFieldDto'];

export type ParagraphTtsSettingsSchema = Schema['ParagraphTtsSettingsSchema'];
export type VoiceDesignSettingsSchema = Schema['VoiceDesignSettingsSchema'];
export type TranscriptionSettingsSchema = Schema['TranscriptionSettingsSchema'];
export type SemanticSimilaritySettingsSchema = Schema['SemanticSimilaritySettingsSchema'];

/** Any provider area's settings schema: what the shared provider page, store and editor carry. */
export type ProviderSettingsSchema =
  | ParagraphTtsSettingsSchema
  | VoiceDesignSettingsSchema
  | TranscriptionSettingsSchema
  | SemanticSimilaritySettingsSchema;

export type CharacterVoicesDto = Schema['CharacterVoicesDto'];

/**
 * Optional `generate-audio` body: an unsaved prompt to design from, stored with the take. The
 * handler reads it by hand, so the document has it inline on its operation, not as a component.
 */
export type GenerateVoiceAudioRequest = NonNullable<
  paths['/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio']['post']['requestBody']
>['content']['application/json'];

/** `referenceSeconds` / `referenceWarning` as `VoiceDto` reports them for the stored take. */
export type GenerateVoiceAudioResponse = Schema['GenerateVoiceAudioResponse'];

export type VoiceBatchStartRequest = Schema['VoiceBatchStartRequest'];
export type VoiceBatchStatusDto = Schema['VoiceBatchStatusDto'];

/** 202 body for the voice-batch, assembly and book-edit starts. */
export type StartedResponse = Schema['StartedResponse'];

// ---- Assembly (`AssemblyEndpoints.cs`) --------------------------------------------------------------

export type AssemblyStartRequest = Schema['AssemblyStartRequest'];

export type AssemblyPhase = 'Gather' | 'Silence' | 'ProbeConcat' | 'Encode' | 'Finalize';

export interface AssemblyStatusDto {
  isRunning: boolean;
  currentPhase: AssemblyPhase | null;
  encodePercent: number;
  lastError: string | null;
  audioRemainingCount: number;
  /** Project of the running (or most recent) job; null until the first start. */
  folder: string | null;
  /** Set once that job completed: a file name under its assembly outputs. */
  outputFileName: string | null;
}

/** One assembled audiobook under `<workspace>/{folder}/output/`. */
export interface AssemblyOutputDto {
  fileName: string;
  sizeBytes: number;
  /** ISO 8601, UTC: when the file was last written. */
  createdAt: string;
  /** A build that skipped items without audio (`_partial_` in the name). */
  isPartial: boolean;
}

// ---- Settings entities (`SettingsEndpoints.cs`, `Read2Me.AppData.Entities`) -----------------------

export type LlmServerConfig = Schema['LlmServerConfig'];

// ---- LLM settings page (`LlmSettingsEndpoints.cs`, ticket 21) -------------------------------------

export type LlmModelsResponse = Schema['LlmModelsResponse'];

/** `connectionId` is the live hub connection that receives the run's `llmTest` message. */
export type LlmTestRequest = Schema['LlmTestRequest'];

export type LlmTestStatusResponse = Schema['LlmTestStatusResponse'];

/** One stored chain rung. A null or absent `promptStyle` inherits the config's own. */
export type AttributionChainStepDto = Schema['AttributionChainStepDto'];

export type AttributionChainRequest = Schema['AttributionChainRequest'];

/** A rung as attribution runs it; `promptStyle` is the effective style. */
export type ResolvedChainStepDto = Schema['ResolvedChainStepDto'];

/**
 * The stored chain, plus `resolved` (the chain attribution runs: deduped, and the default config
 * alone when `steps` is empty) and every config a step may name.
 */
export type AttributionChainResponse = Schema['AttributionChainResponse'];

export type TextSubstitutionStep = Schema['TextSubstitutionStep'];

export type ToSentenceCaseConfig = Schema['ToSentenceCaseConfig'];

export type ParagraphTtsServiceConfig = Schema['ParagraphTtsServiceConfig'];

export type VoiceDesignServiceConfig = Schema['VoiceDesignServiceConfig'];

export type TranscriptionServiceConfig = Schema['TranscriptionServiceConfig'];

export type SemanticSimilarityServiceConfig = Schema['SemanticSimilarityServiceConfig'];

/** What every config entity the generic settings areas serve has in common; routes key on `id`. */
export interface SettingsConfig {
  id: number;
  name: string;
}

export type SetActiveRequest = Schema['SetActiveRequest'];

// ---- Provider settings pages (`ProviderSettingsEndpoints.cs`, ticket 22) ---------------------------

/**
 * One text-processing step a TTS config may enable; `builtIn` is false for one of the config's own
 * substitutions, and `options` are the step's own switches.
 */
export type TextStepDto = Schema['TextStepDto'];

/**
 * `text` is the stored override, null while the built-in `default` is in use; `maxLength` is the
 * longest override a save accepts (every designed voice speaks this text).
 */
export type VoiceDesignSampleTextResponse = Schema['VoiceDesignSampleTextResponse'];

/** Null, blank or the default text clears the override. */
export type VoiceDesignSampleTextRequest = Schema['VoiceDesignSampleTextRequest'];

export type VoiceDesignTestRequest = Schema['VoiceDesignTestRequest'];

export type SimilarityTestRequest = Schema['SimilarityTestRequest'];

export type VoiceDesignTestResponse = Schema['VoiceDesignTestResponse'];

export type TranscriptionTestResponse = Schema['TranscriptionTestResponse'];

export type SimilarityTestResponse = Schema['SimilarityTestResponse'];

// ---- Prompts + audio processing (`SettingsEndpoints.cs`) -------------------------------------------

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

export type PromptTemplateRequest = Schema['PromptTemplateRequest'];

export type PromptPreviewRequest = Schema['PromptPreviewRequest'];

export type PromptPreviewResponse = Schema['PromptPreviewResponse'];

/**
 * `GET /api/settings/prompts/catalog`: one prompt kind as the settings page shows it. `tokens` are
 * names without braces (`book_title`); `expectedResponse` is the JSON shape the LLM must answer
 * with, null for a plain-text prompt (voice); `template` is the stored override, else
 * `defaultTemplate`; `warnings` are compatibility warnings ("Stored override missing …").
 * The host sends `kind` as a string (kinds are kebab-case route segments, not enum members);
 * it is always one of {@link PROMPT_KINDS}, so the client narrows it.
 */
export type PromptCatalogEntry = Omit<Schema['PromptCatalogEntry'], 'kind'> & { kind: PromptKind };

/**
 * `AudioProcessingSettingsService.GetAsync()`; the PUT only accepts the four scalars below.
 * `ffmpegPath` is the ffmpeg the app runs: the saved path, else the configured one, else null (PATH).
 */
export interface AudioProcessingSettings {
  ffmpegPath: string | null;
  savedFfmpegPath: string | null;
  configuredFfmpegPath: string | null;
  werThreshold: number;
  sentenceSplitEnabled: boolean;
  chunkPauseMs: number;
  volumePauseMs: number;
  partPauseMs: number;
  chapterPauseMs: number;
  paragraphPauseMs: number;
  pauseMs: number;
  audioMaxAttempts: number;
}

export interface AudioProcessingUpdateRequest {
  ffmpegPath?: string;
  werThreshold?: number;
  audioMaxAttempts?: number;
  chunkPauseMs?: number;
}

// ---- Audio processing page (`AudioProcessingEndpoints.cs`, ticket 24) ------------------------------

/** The assembler's per-kind silences; `PUT /api/settings/audio-processing/pauses` takes the same shape. */
export interface PauseDurations {
  volumeMs: number;
  partMs: number;
  chapterMs: number;
  paragraphMs: number;
  pauseMs: number;
}

/** The two paragraph post-process steps, in pipeline order. */
export const AUDIO_STEP_IDS = {
  silenceTrim: 'silence-trim',
  consonantSoften: 'consonant-soften',
} as const;

/**
 * One step's stored config (`AudioPostProcessStepConfig`): its enabled flag and the step's own
 * settings payload — `SilenceTrimSettings` or `ConsonantSoftenSettings` below.
 */
export interface AudioPostProcessStepConfig {
  stepId: string;
  enabled: boolean;
  settings: Record<string, unknown> | null;
}

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

/**
 * `GET /api/settings/audio-processing/full`: everything the page shows, in one read. `ffmpegPath` is
 * the saved value the form edits; with none saved the app runs `configuredFfmpegPath` (the host's
 * `Ffmpeg:Path`), else ffmpeg on PATH.
 */
export interface AudioProcessingFull {
  ffmpegPath: string | null;
  configuredFfmpegPath: string | null;
  werThreshold: number;
  sentenceSplitEnabled: boolean;
  chunkPauseMs: number;
  audioMaxAttempts: number;
  pauses: PauseDurations;
  steps: AudioPostProcessStepConfig[];
}

export interface FfmpegTestRequest {
  /** Persisted before the probe; blank means "rely on PATH". */
  ffmpegPath?: string | null;
}

export interface FfmpegProbeResult {
  success: boolean;
  message: string;
}

/** One row of the A/B preview picker: a generated item that still holds a Preview Source. */
export interface RecentAudioSample {
  itemId: Guid;
  folder: string;
  text: string;
  characterName: string | null;
  voiceName: string | null;
  projectTitle: string;
}

export interface PreviewSampleRef {
  folder: string;
  itemId: Guid;
}

export interface StepPreviewRequest {
  sample: PreviewSampleRef;
  /** The card's *unsaved* settings payload. */
  settings: Record<string, unknown> | null;
}

/** `appliedOk` false means the step fell back: `processedUrl` plays the unprocessed audio and `reason` says why. */
export interface StepPreviewResponse {
  previewId: string;
  originalUrl: string;
  processedUrl: string;
  removedMs: number | null;
  reason: string | null;
  appliedOk: boolean;
}

// ---- AI services (`AiServiceEndpoints.cs`) ----------------------------------------------------------

export interface AiServiceDto {
  name: string;
  containerName: string;
  baseUrl: string;
  usesGpu: boolean;
}

export type AiServiceStatus =
  | 'NotFound'
  | 'Stopped'
  | 'Starting'
  | 'Ready'
  | 'Recovering'
  | 'Down'
  | 'Unknown';

export interface AiServiceStatusDto {
  name: string;
  status: AiServiceStatus;
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

/** `ready` means nothing to start and nothing to stop: the task may run now. */
export interface PreflightPlanDto {
  ready: boolean;
  toStart: { name: string; status: AiServiceStatus }[];
  conflicts: { name: string; reason: string }[];
}

export interface PreflightRunRequest {
  connectionId?: string | null;
}

export interface PreflightRunResponse {
  run: string;
}

// ---- Themes (`ThemeEndpoints.cs`, ticket 04) --------------------------------------------------------

/** A theme row; the optional colours are null where the palette default applies. */
export type AppTheme = Schema['AppTheme'];

export type ThemeSelectionDto = Schema['ThemeSelectionDto'];

/** Both fields optional, so a client can flip one without knowing the other. */
export type ThemeSelectionUpdateRequest = Schema['ThemeSelectionUpdateRequest'];

// ---- Cast (`CharacterEndpoints.cs`, ticket 15) ---------------------------------------------------

/**
 * A cast-list row. `isNarrator` marks the seed Narrator row; `narratesBook` the character the
 * narrator link points at. Aliases carry their ids so the detail can offer removal directly.
 */
export interface CharacterSummaryDto {
  id: Guid;
  name: string;
  aliases: CharacterAliasDto[];
  lineCount: number;
  voiceCount: number;
  readyVoiceCount: number;
  isNarrator: boolean;
  narratesBook: boolean;
}

/** One item a character speaks, with where it sits so the reader can open there. */
export interface CharacterLineDto {
  itemId: Guid;
  paragraphId: Guid;
  chapterId: Guid;
  text: string;
}

/** `speaker` is null on a dialog item whose speaker is not yet attributed, and on narration. */
export interface ContextItemDto {
  itemId: Guid;
  text: string;
  isDialog: boolean;
  speaker: string | null;
}

export interface ContextParagraphDto {
  text: string;
  items: ContextItemDto[];
}

/** Nearest neighbour last in `before`, first in `after`. */
export interface ParagraphContextDto {
  before: ContextParagraphDto[];
  paragraph: ContextParagraphDto;
  after: ContextParagraphDto[];
}

// ---- Voice audio editor (`VoiceEditorEndpoints.cs`) ------------------------------------------------

/**
 * One step of the voice editor's checklist: its dials are `SettingsFieldDto`s so
 * `r2m-settings-form` renders them; `defaults` holds the dial keys' starting values.
 */
export type StepCatalogEntryDto = Schema['StepCatalogEntryDto'];

export type PreviewStepRequest = Schema['PreviewStepRequest'];
export type PreviewRequest = Schema['PreviewRequest'];

/** `applied` false means the step fell back to its input; `reason` says why. */
export type PreviewStageDto = Schema['PreviewStageDto'];

export type PreviewResponse = Schema['PreviewResponse'];
export type ApplyPreviewRequest = Schema['ApplyPreviewRequest'];

// ---- AI book edits (`BookEditEndpoints.cs`) --------------------------------------------------------

/** `EditPlanStatus`: `Ok` is the only status that carries a program; the rest explain why there is nothing to do. */
export type EditPlanStatus =
  | 'Ok'
  | 'NoLlmConfigured'
  | 'Unsupported'
  | 'ServiceUnavailable'
  | 'Failed'
  | 'NoTargets';

/** `TransformKind`: only `Llm` plans send a request per item, so only they offer per-row retries. */
export type BookEditTransformKind = 'RegexReplace' | 'SetTemplate' | 'ChangeCase' | 'Llm';

export type ProposalStatus = 'Proposed' | 'NoChange' | 'Failed';

export type BookEditRunStatus = 'Idle' | 'Running' | 'Completed' | 'Cancelled' | 'Failed';

export interface PlanBookEditRequest {
  instruction: string;
  thinking: boolean;
}

/** `program` is the opaque session id every later call names; null unless `status` is `Ok`. */
export interface PlanBookEditResponse {
  status: EditPlanStatus;
  reason: string | null;
  summary: string | null;
  program: string | null;
  transform: BookEditTransformKind | null;
  targetCount: number;
  /** LLM requests the proposal will send; 0 for a deterministic plan. */
  requestCount: number;
  warnings: string[];
}

/** `connectionId` names the hub connection the run's `bookEdit` messages go to. */
export interface ProposeBookEditRequest {
  thinking: boolean;
  connectionId?: string | null;
}

export interface ProposeOneBookEditRequest {
  targetId: Guid;
  hint?: string | null;
  thinking: boolean;
}

/** `ProposedEdit` on the wire; `newValue` is null on a `Failed` row. */
export interface BookEditRow {
  kind: BookEditTargetKind;
  id: Guid;
  displayPath: string;
  oldValue: string;
  newValue?: string | null;
  status: ProposalStatus;
  failureReason?: string | null;
}

/** The session's proposal run — what a client that missed the hub reads back. */
export interface BookEditRunDto {
  status: BookEditRunStatus;
  done: number;
  total: number;
  rows: BookEditRow[];
  reason: string | null;
}

/** `HistoryStepKind`: what opened a History step. */
export type HistoryStepKind = Schema['HistoryStepKind'];

/** `StepAction`: the kind of action a request's or Background run's step is (the host's `StepAction`). */
export type StepAction = Schema['StepAction'];

/** `HistoryStep`: one History step. `sequence` is for display; the line is the `parentStepId` chain from the Head. */
export type HistoryStep = Schema['HistoryStep'];

/** `GET /api/projects/{folder}/history`: steps newest first, the Head, the busy reason and the restoring flag. */
export type HistoryDto = Schema['HistoryDto'];

/** `GET /api/projects/{folder}/history/{stepId}`: the step plus what a Restore to it would cost now. */
export type HistoryStepDto = Schema['HistoryStepDto'];

/** `POST /api/projects/{folder}/history/{stepId}/restore`: the Restore step (null when nothing changed) and the audio counts. */
export type RestoreResultDto = Schema['RestoreResultDto'];

// ---- Pronunciations (`PronunciationEndpoints.cs`) ---------------------------------------------------

/** `GET /api/projects/{folder}/pronunciations`: one entry and its reach in the book. */
export interface PronunciationDto {
  id: Guid;
  writtenForm: string;
  spokenForm: string;
  /** Matched spans in the book. */
  matchCount: number;
  /** Items holding a match. */
  itemCount: number;
  /** Those items that have audio. */
  withAudioCount: number;
}

/** `POST …/pronunciations` and `PUT …/pronunciations/{id}`. */
export interface SetPronunciationRequest {
  writtenForm: string;
  spokenForm: string;
}

/** A saved entry with its reach, and what saving it cost. */
export interface PronunciationSavedDto extends PronunciationDto {
  /** Items that lost their audio. */
  audioDiscardCount: number;
  /** Spoken items without audio after the save: what needs generating. */
  needsAudioCount: number;
}

/** `DELETE …/pronunciations/{id}`. */
export interface PronunciationDeletedDto {
  audioDiscardCount: number;
  needsAudioCount: number;
}

/** `POST …/pronunciations/impact`: no id adds, an id edits that entry, `delete` deletes it. */
export interface PronunciationDraftRequest {
  id: Guid | null;
  writtenForm: string | null;
  spokenForm: string | null;
  delete: boolean;
}

export interface PronunciationRef {
  id: Guid;
  writtenForm: string;
  spokenForm: string;
}

/** An entry whose written form holds the draft's as whole words, or is held by it. */
export interface PronunciationOverlap extends PronunciationRef {
  /** The draft's written form is the longer, so it wins where both match. */
  draftWins: boolean;
}

/** One match of the draft, in book order. */
export interface PronunciationMatchInBook {
  itemId: Guid;
  chapterId: Guid;
  chapterTitle: string | null;
  speaker: string | null;
  hasAudio: boolean;
  /** The sentence around the match; `matchStart`/`matchLength` are offsets in it. */
  sentence: string;
  /** Where the sentence starts in the item's text: what a try-out names it by. */
  sentenceStart: number;
  matchStart: number;
  matchLength: number;
  /** The sentence with the drafted list applied. */
  spokenSentence: string;
  /** Where the match's spoken form (its case carried over) sits in `spokenSentence`. */
  spokenMatchStart: number;
  spokenMatchLength: number;
}

/** What saving a draft would do. */
export interface PronunciationImpactDto {
  matchCount: number;
  itemCount: number;
  withAudioCount: number;
  /** Exactly the audio a save would discard. */
  audioDiscardCount: number;
  overlaps: PronunciationOverlap[];
  /** Another entry with the written form, ignoring case: a save would be 409. */
  duplicateOf: PronunciationRef | null;
  matches: PronunciationMatchInBook[];
}

/** A try-out's voice: one of the project's voices, or the item's own resolved voice. */
export type TryVoice = { voiceId: Guid } | 'item';

/**
 * `POST …/pronunciations/try`: the item's sentence starting at `sentenceStart` (an impact match's),
 * with `draft` in its entry's place unless `asWritten`. Answers the WAV body.
 */
export interface TryPronunciationRequest {
  itemId: Guid;
  sentenceStart: number;
  draft: PronunciationDraftRequest | null;
  asWritten: boolean;
  voice: TryVoice;
}

/** One span of an item's text the list matches (`GET …/pronunciations/matches`): offsets into the item's text. */
export interface PronunciationSpan {
  itemId: Guid;
  start: number;
  length: number;
  pronunciationId: Guid;
  /** Where the sentence around it starts in the item's text: what a try-out names it by. */
  sentenceStart: number;
}

/** `GET …/pronunciations/matches?chapterIds=…`: the spans in book order and the entries they name. */
export interface PronunciationSpanReport {
  pronunciations: PronunciationRef[];
  spans: PronunciationSpan[];
}

// ---- Setup check (`SetupCheckEndpoints.cs`) ---------------------------------------------------------

/** `SetupCheckGroup`: the area a check sits under in the report's one table. */
export type SetupCheckGroup = Schema['SetupCheckGroup'];

/** `SetupCheckTier`: the Quick check reads; the Full check sends real requests. */
export type SetupCheckTier = Schema['SetupCheckTier'];

/** `SetupCheckResult`: a check's one result; `Checking` is transient, while a run is on the row. */
export type SetupCheckResult = Schema['SetupCheckResult'];

/** `SetupCheckLane`: where a Full check step runs — on this machine, in a CPU container, or on the GPU. */
export type SetupCheckLane = Schema['SetupCheckLane'];

/**
 * The Full check going on: step `step` of `total`, its lane and what it is doing. `cancelling`: a
 * cancel was asked for, so the run stops once this step ends.
 */
export interface SetupFullRun {
  step: number;
  total: number;
  lane: SetupCheckLane;
  label: string;
  cancelling: boolean;
}

/** The in-app Settings section that owns the value: an app-relative path and its label. */
export interface SetupSettingsLink {
  path: string;
  label: string;
}

/** One sentence, plus a Settings link and/or a command or file path to copy. */
export interface SetupFixHint {
  text: string;
  link: SetupSettingsLink | null;
  copy: string | null;
}

/**
 * Re-running one Full-tier check. `gpu`: it drives a GPU container, so it stops the one running and
 * loads a model, and the page confirms first.
 */
export interface SetupRerun {
  gpu: boolean;
}

/**
 * One check: what the app expected and what it found. A note never changes the result. `managed`:
 * the app can start and restart what the row checks (a docker AI service, or a config on one).
 * `rerun`: what re-running this one check takes; null when it can't be (a Quick-tier row, or a
 * config with no active one).
 */
export interface SetupCheckRow {
  id: string;
  group: SetupCheckGroup;
  name: string;
  tier: SetupCheckTier;
  result: SetupCheckResult;
  expected: string;
  found: string;
  note: string | null;
  fix: SetupFixHint;
  managed: boolean;
  rerun: SetupRerun | null;
}

/**
 * `GET /api/setup-check`: the rows in table order and when each tier last ran (null: not yet).
 * `firstRun`: this host start created app.db. `startedAt`: when this host started, which keys the
 * start banner's dismissal. `noWorkspace`: this host started without a usable Workspace, so every
 * other API call answers 503 "No Workspace" and every route shows the Setup check until a restart.
 * `fullRun`: the Full check going on, null when none is. `fullCheckEstimateSeconds`: how long a Full
 * check is likely to take, for its confirm. `putBack`: after a Full check, what it did with the
 * containers it drove ("Put back: read2me-whisper stopped again."); null before one.
 */
export interface SetupCheckReport {
  rows: SetupCheckRow[];
  quickCheckedAt: string | null;
  fullCheckedAt: string | null;
  firstRun: boolean;
  startedAt: string;
  noWorkspace: boolean;
  fullRun: SetupFullRun | null;
  fullCheckEstimateSeconds: number;
  putBack: string | null;
}
