/**
 * Wire shapes of `/hubs/live` (spec D5 "Live hub contract"), mirrored by hand from
 * `Read2Me.App/Live/LiveMessages.cs` + `LiveMessageMapper.cs` + `ILiveClient.cs`. JSON conventions
 * (LiveServiceCollectionExtensions.cs): camelCase properties, enums as member names, nulls omitted
 * — so a nullable C# field is `?: T | null` here. `live-messages.spec.ts` reads the C# sources and
 * fails when a family name or a `kind` string drifts.
 */

import { Guid, NodeStatusSummaryDto, QueueItemStatus } from '@app/api';

// ---- families (ILiveClient.cs [HubMethodName]) --------------------------------------------------

export const LIVE_FAMILIES = [
  'queue',
  'nodeStatus',
  'itemStatus',
  'receipt',
  'assembly',
  'voiceBatch',
  'watchdog',
  'llm',
  'audioGen',
  'throughput',
  'settingsChanged',
] as const;

export type LiveFamily = (typeof LIVE_FAMILIES)[number];

/** Client → server hub methods (LiveHub.cs). */
export const LIVE_HUB_METHODS = {
  joinProject: 'JoinProject',
  leaveProject: 'LeaveProject',
  joinStream: 'JoinStream',
  leaveStream: 'LeaveStream',
  getSnapshot: 'GetSnapshot',
} as const;

export type StreamKind = 'llm' | 'audio';

// ---- kind discriminators (LiveMessageMapper.cs) -------------------------------------------------

export const LIVE_KINDS = {
  assembly: ['phaseStarted', 'progress', 'completed', 'failed', 'cancelled'],
  voiceBatch: ['started', 'progress', 'voiceUpdated', 'completed', 'cancelled'],
  watchdog: ['recoveryStarted', 'containerRestarted', 'serviceHealthy', 'serviceDown'],
  llm: [
    'runStarted',
    'runEnded',
    'requestStarted',
    'delta',
    'streamCompleted',
    'streamFailed',
    'streamAborted',
    'escalationStarted',
  ],
  audioGen: [
    'itemStarted',
    'audioGenerated',
    'normalized',
    'postProcessed',
    'transcribed',
    'verified',
    'failed',
  ],
} as const;

export type AssemblyKind = (typeof LIVE_KINDS.assembly)[number];
export type VoiceBatchKind = (typeof LIVE_KINDS.voiceBatch)[number];
export type WatchdogKind = (typeof LIVE_KINDS.watchdog)[number];
export type LlmKind = (typeof LIVE_KINDS.llm)[number];
export type AudioGenKind = (typeof LIVE_KINDS.audioGen)[number];

// ---- queue (group global, debounced) ------------------------------------------------------------

/** `CharacterQueueService.QueueSnapshot`. */
export interface AttributionQueueState {
  queuedCount: number;
  processingCount: number;
  averageSecondsPerParagraph: number;
  estimatedSecondsRemaining: number;
  completedCount: number;
  currentItemElapsedSeconds: number;
  isBusy: boolean;
}

/** `AudioQueueService.AudioQueueSnapshot`. */
export interface AudioQueueState {
  queuedCount: number;
  processingCount: number;
  averageSecondsPerItem: number;
  estimatedSecondsRemaining: number;
  completedCount: number;
  currentItemElapsedSeconds: number;
}

/** The latched "escalating N items → config (step S)" banner; absent when nothing is escalating. */
export interface EscalationState {
  step: number;
  configName?: string | null;
  itemCount: number;
}

export interface QueueMessage {
  attribution: AttributionQueueState;
  audio: AudioQueueState;
  escalation?: EscalationState | null;
}

// ---- nodeStatus / itemStatus (group project:{folder}, debounced deltas) -------------------------

/** `NodeStatusService.NodeStatusSummary`: the same record `GET /api/projects/{folder}/status` returns. */
export type NodeStatusSummary = NodeStatusSummaryDto;

/** A `null` entry means the node no longer rolls up. */
export interface NodeStatusMessage {
  folder: string;
  nodes: Record<string, NodeStatusSummary | null>;
  folderAudioRemaining: number;
}

export type OutcomeKindName = 'Failed' | 'Unfinished';

export interface OutcomeEntry {
  kind: OutcomeKindName;
  reason?: string | null;
}

export interface ParagraphStatusEntry {
  status?: QueueItemStatus | null;
  outcome?: OutcomeEntry | null;
}

export type AudioReviewState = 'NeedsReview' | 'Dismissed';

/** `AudioReviewService.AudioReviewInfo`. */
export interface AudioReviewInfo {
  state: AudioReviewState;
  normalizeOk: boolean;
  normalizeReason?: string | null;
  verifyOk: boolean;
  wer?: number | null;
  verifyReason?: string | null;
  transcript?: string | null;
  originalTextSnapshot?: string | null;
}

export interface ItemStatusEntry {
  status?: QueueItemStatus | null;
  outcome?: OutcomeEntry | null;
  audioVersion?: number | null;
  review?: AudioReviewInfo | null;
}

/** A `null` entry means the queue forgot the paragraph/item. */
export interface ItemStatusMessage {
  folder: string;
  paragraphs: Record<string, ParagraphStatusEntry | null>;
  items: Record<string, ItemStatusEntry | null>;
}

// ---- receipt (group project:{folder}) -----------------------------------------------------------

export type BookMutationScope = 'Exact' | 'WholeProject';

/** `BookFacets` flag names; the wire value is the comma-joined `[Flags]` string (`"Structure, Audio"`). */
export const BOOK_FACETS = [
  'Structure',
  'ItemText',
  'Attribution',
  'Audio',
  'Reviews',
  'Characters',
  'Narrator',
  'Voices',
  'VoiceRules',
  'ProjectPolicy',
  'NodeTitle',
] as const;

export type BookFacet = (typeof BOOK_FACETS)[number];

/** `"None"`, `"All"`, one facet name, or a comma-separated combination. */
export type BookFacetsValue = string;

export function hasFacet(facets: BookFacetsValue, facet: BookFacet): boolean {
  if (facets === 'All') return true;
  return facets
    .split(',')
    .map((f) => f.trim())
    .includes(facet);
}

export interface BookStructuralRelation {
  kind: 'Split' | 'Merge';
  sourceId: Guid;
  resultId: Guid;
}

export interface BookMutationEffects {
  scope: BookMutationScope;
  facets: BookFacetsValue;
  createdId?: Guid | null;
  nodeIds: Guid[];
  paragraphIds: Guid[];
  paragraphItemIds: Guid[];
  structural: BookStructuralRelation[];
  changedNothing: boolean;
}

export interface ReceiptMessage {
  folder: string;
  /** Verbatim mutation class name, e.g. `CreateCharacterMutation`. */
  mutationName: string;
  mutationId: Guid;
  revision: number;
  effects: BookMutationEffects;
  originId: Guid;
}

/** {@link ReceiptMessage} with the client's own-write flag (from `X-Origin-Id`). */
export interface Receipt extends ReceiptMessage {
  isOwn: boolean;
}

// ---- assembly (group global) --------------------------------------------------------------------

export interface AssemblyMessage {
  kind: AssemblyKind;
  /** `AssemblyPhase` member name; on `phaseStarted`. */
  phase?: string | null;
  /** 0..1; on `progress`. */
  fraction?: number | null;
  /** On `failed`. */
  reason?: string | null;
}

export interface AssemblyState {
  isRunning: boolean;
  currentPhase?: string | null;
  encodePercent: number;
  lastError?: string | null;
  audioRemainingCount: number;
}

// ---- voiceBatch (group global) ------------------------------------------------------------------

export interface VoiceBatchMessage {
  kind: VoiceBatchKind;
  operation?: string | null;
  processed?: number | null;
  total?: number | null;
  failed?: number | null;
  currentVoiceName?: string | null;
  characterId?: Guid | null;
  voiceId?: Guid | null;
  designPrompt?: string | null;
  audioFileName?: string | null;
  transcript?: string | null;
}

export interface VoiceBatchState {
  isRunning: boolean;
  processed: number;
  total: number;
  failed: number;
  currentVoiceName?: string | null;
  currentOperation?: string | null;
  lastError?: string | null;
}

// ---- watchdog (group global) --------------------------------------------------------------------

export interface WatchdogMessage {
  kind: WatchdogKind;
  service: string;
  reason?: string | null;
}

/** Last known watchdog kind per service name. */
export type WatchdogState = Record<string, WatchdogKind | string>;

// ---- llm (group stream:llm) ---------------------------------------------------------------------

export interface LlmMessage {
  kind: LlmKind;
  /** `delta`: a 100 ms batch of thinking text. */
  thinking?: string | null;
  /** `delta`: a 100 ms batch of content text. */
  content?: string | null;
  paragraphPreview?: string | null;
  prompt?: string | null;
  configId?: number | null;
  configName?: string | null;
  tokensIn?: number | null;
  tokensOut?: number | null;
  generationMs?: number | null;
  tokensPerSecond?: number | null;
  reason?: string | null;
  step?: number | null;
  itemCount?: number | null;
}

// ---- audioGen (group stream:audio) --------------------------------------------------------------

export interface AudioGenMessage {
  kind: AudioGenKind;
  id: Guid;
  attempt: number;
  character?: string | null;
  text?: string | null;
  ok?: boolean | null;
  reason?: string | null;
  stepId?: string | null;
  applied?: boolean | null;
  transcript?: string | null;
  wer?: number | null;
  rescued?: boolean | null;
}

// ---- throughput (group global, 1 s while a run is active) ---------------------------------------

export interface ConfigThroughput {
  configId: number;
  configName: string;
  requests: number;
  tokensOut?: number | null;
  generationMs?: number | null;
  tokensPerSecond?: number | null;
}

/** `ThroughputAggregator.ThroughputSnapshot`. */
export interface ThroughputSnapshot {
  hasRun: boolean;
  isRunActive: boolean;
  runThroughput?: number | null;
  perConfig: ConfigThroughput[];
  generationRate?: number | null;
  /** 20 × 500 ms buckets, oldest first; `null` = nothing measured in that bucket. */
  generationRateHistory: (number | null)[];
}

// ---- settingsChanged (group global) -------------------------------------------------------------

export interface SettingsChangedMessage {
  area: string;
}

// ---- snapshots ----------------------------------------------------------------------------------

/** Answer to `JoinProject` and one entry of {@link LiveSnapshot.projects}. */
export interface ProjectSnapshot {
  folder: string;
  revision: number;
  nodes: Record<string, NodeStatusSummary | null>;
  folderAudioRemaining: number;
  paragraphs: Record<string, ParagraphStatusEntry | null>;
  items: Record<string, ItemStatusEntry | null>;
}

/** Answer to `GetSnapshot`. */
export interface LiveSnapshot {
  queue: QueueMessage;
  assembly: AssemblyState;
  voiceBatch: VoiceBatchState;
  watchdog: WatchdogState;
  throughput: ThroughputSnapshot;
  projects: Record<string, ProjectSnapshot>;
}

// ---- family → payload ---------------------------------------------------------------------------

export interface LiveMessageMap {
  queue: QueueMessage;
  nodeStatus: NodeStatusMessage;
  itemStatus: ItemStatusMessage;
  receipt: ReceiptMessage;
  assembly: AssemblyMessage;
  voiceBatch: VoiceBatchMessage;
  watchdog: WatchdogMessage;
  llm: LlmMessage;
  audioGen: AudioGenMessage;
  throughput: ThroughputSnapshot;
  settingsChanged: SettingsChangedMessage;
}
