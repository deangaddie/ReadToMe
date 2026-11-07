/**
 * Wire shapes of `/hubs/live`. The host puts every hub message into the same OpenAPI document as its
 * HTTP contract (ADR 0017, #238), so each type here is an alias, `Schema['X']`, under its C# name, and
 * `bun run typecheck` catches a field the host renames or retypes once `pwsh scripts/api-types.ps1`
 * regenerates the contract. The wire form is the HTTP one: camelCase properties, enums as PascalCase
 * member names, nulls sent — so a nullable field is `T | null`, never missing.
 *
 * What stays hand-written is what has no wire counterpart: the client's own-write flag on a
 * {@link Receipt}, the stream names, {@link hasFacet}, and the two lists the connection walks,
 * each checked against the document.
 */

import type { Schema } from '@app/api';

// ---- families and hub methods -------------------------------------------------------------------

/** Each family (`connection.on` name) and the payload it carries: `ILiveClient`'s `[HubMethodName]`s. */
export type LiveMessageMap = Schema['LiveMessageMap'];

export type LiveFamily = keyof LiveMessageMap;

/** True only when `A` and `B` are the same union, so a list below can't miss a member. */
type Same<A, B> = [A] extends [B] ? ([B] extends [A] ? true : false) : false;

export const LIVE_FAMILIES = [
  'queue',
  'nodeStatus',
  'itemStatus',
  'receipt',
  'assembly',
  'voiceBatch',
  'watchdog',
  'serviceStatus',
  'preflight',
  'llm',
  'audioGen',
  'throughput',
  'settingsChanged',
  'series',
  'bookEdit',
  'llmTest',
  'history',
  'setupCheck',
] as const satisfies readonly LiveFamily[];

/** Client → server hub methods (`LiveHub`'s public methods). */
export const LIVE_HUB_METHODS = {
  joinProject: 'JoinProject',
  leaveProject: 'LeaveProject',
  joinStream: 'JoinStream',
  leaveStream: 'LeaveStream',
  getSnapshot: 'GetSnapshot',
} as const satisfies Record<string, Schema['LiveHubMethod']>;

const everyFamilyListed: Same<(typeof LIVE_FAMILIES)[number], LiveFamily> = true;
const everyHubMethodListed: Same<
  (typeof LIVE_HUB_METHODS)[keyof typeof LIVE_HUB_METHODS],
  Schema['LiveHubMethod']
> = true;
void everyFamilyListed;
void everyHubMethodListed;

/** What `JoinStream` / `LeaveStream` take; the hub refuses anything else. */
export type StreamKind = 'llm' | 'audio';

// ---- queue (group global, debounced) ------------------------------------------------------------

export type QueueMessage = Schema['QueueMessage'];
export type AttributionQueueSnapshot = Schema['AttributionQueueSnapshot'];
export type AudioQueueSnapshot = Schema['AudioQueueSnapshot'];
/** The latched "escalating N items → config (step S)" banner; null when nothing is escalating. */
export type EscalationState = Schema['EscalationState'];

// ---- nodeStatus / itemStatus (group project:{folder}, debounced deltas) -------------------------

/** A `null` node means it no longer rolls up. */
export type NodeStatusMessage = Schema['NodeStatusMessage'];
/** A `null` entry means the queue forgot the paragraph or item. */
export type ItemStatusMessage = Schema['ItemStatusMessage'];
export type ParagraphStatusEntry = Schema['ParagraphStatusEntry'];
export type ItemStatusEntry = Schema['ItemStatusEntry'];

// ---- receipt (group project:{folder}) -----------------------------------------------------------

export type ReceiptMessage = Schema['ReceiptMessage'];
export type ReceiptEffects = Schema['ReceiptEffects'];
/** One facet of a write; a receipt lists each one it touched. */
export type BookFacets = Schema['BookFacets'];

/** {@link ReceiptMessage} with the client's own-write flag (from `X-Origin-Id`). */
export interface Receipt extends ReceiptMessage {
  isOwn: boolean;
}

export function hasFacet(facets: readonly BookFacets[], facet: BookFacets): boolean {
  return facets.includes(facet);
}

// ---- the other families -------------------------------------------------------------------------

export type AssemblyMessage = Schema['AssemblyMessage'];
export type AssemblyState = Schema['AssemblyState'];
export type VoiceBatchMessage = Schema['VoiceBatchMessage'];
export type VoiceBatchState = Schema['VoiceBatchState'];
export type WatchdogMessage = Schema['WatchdogMessage'];
export type WatchdogKind = Schema['WatchdogKind'];
export type ServiceStatusMessage = Schema['ServiceStatusMessage'];
export type AiServiceOp = Schema['AiServiceOp'];
export type PreflightMessage = Schema['PreflightMessage'];
export type PreflightStage = Schema['PreflightStage'];
export type LlmMessage = Schema['LlmMessage'];
export type AudioGenMessage = Schema['AudioGenMessage'];
export type ThroughputSnapshot = Schema['ThroughputSnapshot'];
export type SettingsChangedMessage = Schema['SettingsChangedMessage'];
/** A series registry changed or the series was deleted: read `/api/series` again. */
export type SeriesMessage = Schema['SeriesMessage'];
export type BookEditMessage = Schema['BookEditMessage'];
export type LlmTestMessage = Schema['LlmTestMessage'];
export type HistoryMessage = Schema['HistoryMessage'];
export type SetupCheckMessage = Schema['SetupCheckMessage'];

// ---- snapshots ----------------------------------------------------------------------------------

/** Answer to `JoinProject` and one entry of {@link LiveSnapshot.projects}. */
export type ProjectSnapshot = Schema['ProjectSnapshot'];
/** Answer to `GetSnapshot`. */
export type LiveSnapshot = Schema['LiveSnapshot'];
/** Last watchdog transition per service name. */
export type WatchdogState = LiveSnapshot['watchdog'];
/** Last observed `AiServiceStatus` per service name. */
export type ServiceStatusState = LiveSnapshot['serviceStatus'];
