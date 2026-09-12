/**
 * Wire shapes for the Agent API (spec D8, ticket 05).
 *
 * The host's OpenAPI document describes request bodies and the settings entities but declares no
 * response content (every endpoint returns `Results.Ok(...)` untyped), so `schema.d.ts` cannot
 * type what comes back. Response DTOs are therefore hand-written here, mirrored from the C# records
 * in `src/Read2Me.App/Api/*.cs`, and every shape that *does* exist in the generated schema is pinned
 * to it in `schema-pins.ts` so a renamed or removed field fails `npm run typecheck`.
 *
 * Conventions: the host serialises with Minimal API defaults: camelCase names, `Guid` as string,
 * entity enums as their integer value, `ToString()`-ed enums as their member name.
 */
import type { components } from './schema';

/** Component schemas the host's OpenAPI document declares (request bodies + settings entities). */
export type Schema = components['schemas'];

/** A `System.Guid` on the wire. */
export type Guid = string;

/** Route segment for node-scoped endpoints (`/nodes/{level}/…`, enqueue requests). */
export type NodeLevel = 'volume' | 'part' | 'chapter';

// ---- Integer enums (settings entities serialise the numeric value) ---------------------------------

export const LlmApiType = { OpenAiCompatible: 0 } as const;
export type LlmApiType = (typeof LlmApiType)[keyof typeof LlmApiType];

export const AttributionPromptStyle = { Full: 0, Simple: 1 } as const;
export type AttributionPromptStyle =
  (typeof AttributionPromptStyle)[keyof typeof AttributionPromptStyle];

export const ParagraphTtsServiceType = {
  VoxCpm2: 0,
  Chatterbox: 1,
  ChatterboxTurbo: 2,
  Qwen3Base: 3,
} as const;
export type ParagraphTtsServiceType =
  (typeof ParagraphTtsServiceType)[keyof typeof ParagraphTtsServiceType];

export const VoiceDesignServiceType = { VoxCpm2: 0, Qwen3: 1 } as const;
export type VoiceDesignServiceType =
  (typeof VoiceDesignServiceType)[keyof typeof VoiceDesignServiceType];

export const TranscriptionServiceType = { LocalWhisper: 0 } as const;
export type TranscriptionServiceType =
  (typeof TranscriptionServiceType)[keyof typeof TranscriptionServiceType];

export const SemanticSimilarityServiceType = { MiniLmL6: 0, MpnetBaseV2: 1 } as const;
export type SemanticSimilarityServiceType =
  (typeof SemanticSimilarityServiceType)[keyof typeof SemanticSimilarityServiceType];

/** `ParagraphOutcomeKind` / `AudioItemOutcomeKind`: same members, same numbers. */
export const OutcomeKind = { Failed: 0, Unfinished: 1 } as const;
export type OutcomeKind = (typeof OutcomeKind)[keyof typeof OutcomeKind];

// ---- Projects (`ProjectEndpoints.cs`) -------------------------------------------------------------

export interface ProjectSummary {
  folderName: string;
  title: string;
  author: string | null;
  coverImage: string | null;
  audioItemTotal: number;
  audioItemDone: number;
  audioPercent: number;
}

/** Multipart fields for `POST /api/projects`; `title` and `file` are required by the host. */
export interface CreateProjectRequest {
  title: string;
  bookTitle?: string;
  author?: string;
  file: File;
}

export interface CreateProjectResponse {
  folderName: string;
}

export interface NarratorDto {
  characterId: Guid;
  displayName: string;
  isLinked: boolean;
}

export interface ProjectDetailDto {
  folderName: string;
  title: string;
  bookTitle: string;
  author: string;
  filename: string;
  fileType: string;
  coverImage: string | null;
  narratorOnlyMode: boolean;
  narrator: NarratorDto;
}

export type ImportRequest = Schema['ImportRequest'];

// ---- Book reads (`BookEndpoints.cs`) ---------------------------------------------------------------

export interface NodeDto {
  id: Guid;
  title: string | null;
}

export type PauseKind = 'Pause' | 'ParagraphPause' | 'ChapterPause' | 'PartPause' | 'VolumePause';

/** `BookEndpoints.ItemTypeWord`: narration when the speaker is the narrator, dialog otherwise. */
export type ParagraphItemType = 'Narration' | 'Character' | PauseKind;

export interface ParagraphItemDto {
  id: Guid;
  itemType: ParagraphItemType;
  text: string | null;
  characterId: Guid | null;
  audioFileName: string | null;
}

export interface ParagraphDto {
  id: Guid;
  items: ParagraphItemDto[];
}

/** Exactly one of the three lists is present, chosen by the requested level. */
export interface NodeChildrenDto {
  parts: NodeDto[] | null;
  chapters: NodeDto[] | null;
  paragraphs: ParagraphDto[] | null;
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
}

// ---- Commands (`CommandEndpoints.cs`) --------------------------------------------------------------

export interface CommandResponse {
  newEntityId: Guid | null;
}

// ---- Attribution + audio queues (`AttributionEndpoints.cs`, `AudioEndpoints.cs`, `QueueStatusEndpoints.cs`)

export type NodeEnqueueRequest = Schema['NodeEnqueueRequest'];
export type AudioEnqueueRequest = Schema['AudioEnqueueRequest'];

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

/** `CharacterQueueService.QueueSnapshot`. */
export interface AttributionQueueSnapshot {
  queuedCount: number;
  processingCount: number;
  averageSecondsPerParagraph: number;
  estimatedSecondsRemaining: number;
  completedCount: number;
  currentItemElapsedSeconds: number;
  isBusy: boolean;
}

/** `AudioQueueService.AudioQueueSnapshot`. */
export interface AudioQueueSnapshot {
  queuedCount: number;
  processingCount: number;
  averageSecondsPerItem: number;
  estimatedSecondsRemaining: number;
  completedCount: number;
  currentItemElapsedSeconds: number;
}

// ---- Character discovery (`DiscoveryEndpoints.cs`) -------------------------------------------------

/** `DiscoveryStatus` minus `NoLlmConfigured`, which the endpoint turns into a 422. */
export type DiscoveryStatus = 'Ok' | 'Failed' | 'ServiceUnavailable';

export interface DiscoveredCharacterDto {
  name: string;
  aliases: string[];
}

export interface DiscoveryOutcomeDto {
  status: DiscoveryStatus;
  reason: string | null;
  characters: DiscoveredCharacterDto[];
}

export type ApplyDiscoveryRow = Schema['ApplyDiscoveryRow'];

export interface ApplyDiscoveryResponse {
  applied: number;
}

// ---- Voices (`VoiceEndpoints.cs`) ------------------------------------------------------------------

export type VoiceSource = 'Uploaded' | 'Generated';

export interface VoiceDto {
  id: Guid;
  name: string;
  description: string | null;
  source: VoiceSource;
  designPrompt: string | null;
  transcript: string | null;
  audioFileName: string | null;
}

export interface CharacterVoicesDto {
  defaultVoiceId: Guid | null;
  voices: VoiceDto[];
}

export interface GenerateVoiceAudioResponse {
  audioFileName: string;
  transcript: string;
}

export type VoiceBatchStartRequest = Schema['VoiceBatchStartRequest'];

export interface VoiceBatchStatusDto {
  isRunning: boolean;
  processed: number;
  total: number;
  failed: number;
  currentVoiceName: string | null;
  currentOperation: string | null;
  lastError: string | null;
}

/** 202 body for the voice-batch and assembly starts. */
export interface StartedResponse {
  started: boolean;
}

// ---- Assembly (`AssemblyEndpoints.cs`) --------------------------------------------------------------

export type AssemblyStartRequest = Schema['AssemblyStartRequest'];

export type AssemblyPhase = 'Gather' | 'Silence' | 'ProbeConcat' | 'Encode' | 'Finalize';

export interface AssemblyStatusDto {
  isRunning: boolean;
  currentPhase: AssemblyPhase | null;
  encodePercent: number;
  lastError: string | null;
  audioRemainingCount: number;
}

// ---- Settings entities (`SettingsEndpoints.cs`, `Read2Me.AppData.Entities`) -----------------------

export interface LlmServerConfig {
  id: number;
  name: string;
  apiType: LlmApiType;
  baseUrl: string;
  apiKey: string | null;
  model: string | null;
  temperature: number | null;
  topP: number | null;
  maxTokens: number | null;
  frequencyPenalty: number | null;
  presencePenalty: number | null;
  attributionBatchSize: number;
  promptStyle: AttributionPromptStyle;
  supportsModelSwitch: boolean;
}

export interface TextSubstitutionStep {
  id: string;
  paragraphTtsServiceConfigId: number;
  fromText: string;
  toText: string;
  order: number;
}

export interface ToSentenceCaseConfig {
  id: number;
  paragraphTtsServiceConfigId: number;
  paragraphEnabled: boolean;
  wordEnabled: boolean;
  wordMinLength: number;
}

export interface ParagraphTtsServiceConfig {
  id: number;
  name: string;
  type: ParagraphTtsServiceType;
  settingsJson: string;
  enabledStepIds: string[];
  substitutionSteps: TextSubstitutionStep[];
  toSentenceCaseConfig: ToSentenceCaseConfig | null;
}

export interface VoiceDesignServiceConfig {
  id: number;
  name: string;
  type: VoiceDesignServiceType;
  settingsJson: string;
}

export interface TranscriptionServiceConfig {
  id: number;
  name: string;
  type: TranscriptionServiceType;
  settingsJson: string;
}

export interface SemanticSimilarityServiceConfig {
  id: number;
  name: string;
  type: SemanticSimilarityServiceType;
  settingsJson: string;
}

/** What every config entity the generic settings areas serve has in common; routes key on `id`. */
export interface SettingsConfig {
  id: number;
  name: string;
}

export interface SetActiveRequest {
  id: number;
}

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

/** `AudioProcessingSettingsService.GetAsync()`; the PUT only accepts the four scalars below. */
export interface AudioProcessingSettings {
  ffmpegPath: string;
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

// ---- Themes (`ThemeEndpoints.cs`, ticket 04) --------------------------------------------------------

export interface AppTheme {
  id: number;
  name: string;
  isBuiltIn: boolean;
  isDark: boolean;
  primary: string;
  secondary: string;
  background?: string | null;
  surface?: string | null;
  appbarBackground?: string | null;
  drawerBackground?: string | null;
  textPrimary?: string | null;
  textSecondary?: string | null;
}

export interface ThemeSelection {
  selectedThemeId: number | null;
  followSystemPreference: boolean;
}

export type ThemeSelectionUpdate = Partial<ThemeSelection>;
