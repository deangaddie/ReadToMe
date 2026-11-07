/**
 * The hand-written half of `/hubs/live`'s wire shapes. The host puts every hub message into the same
 * OpenAPI document as its HTTP contract (ADR 0017, #238), so each message type comes generated under
 * its C# name and `@app/api` exports it, with the record's XML doc as its JSDoc; `bun run typecheck`
 * catches a field the host renames or retypes once `pwsh scripts/api-types.ps1` regenerates the
 * contract. The wire form is the HTTP one: camelCase properties, enums as PascalCase member names,
 * nulls sent — so a nullable field is `T | null`, never missing.
 *
 * What stays here is what has no wire counterpart: the client's own-write flag on a
 * {@link Receipt}, the stream names, {@link hasFacet}, and the two lists the connection walks,
 * each checked against the document.
 */

import type {
  BookFacets,
  LiveHubMethod,
  LiveMessageMap,
  LiveSnapshot,
  ReceiptMessage,
} from '@app/api';

// ---- families and hub methods -------------------------------------------------------------------

/** A family: a `connection.on` name, keyed in `LiveMessageMap` to the payload it carries. */
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
} as const satisfies Record<string, LiveHubMethod>;

const everyFamilyListed: Same<(typeof LIVE_FAMILIES)[number], LiveFamily> = true;
const everyHubMethodListed: Same<
  (typeof LIVE_HUB_METHODS)[keyof typeof LIVE_HUB_METHODS],
  LiveHubMethod
> = true;
void everyFamilyListed;
void everyHubMethodListed;

/** What `JoinStream` / `LeaveStream` take; the hub refuses anything else. */
export type StreamKind = 'llm' | 'audio';

// ---- receipt (group project:{folder}) -----------------------------------------------------------

/** {@link ReceiptMessage} with the client's own-write flag (from `X-Origin-Id`). */
export interface Receipt extends ReceiptMessage {
  isOwn: boolean;
}

export function hasFacet(facets: readonly BookFacets[], facet: BookFacets): boolean {
  return facets.includes(facet);
}

// ---- snapshots ----------------------------------------------------------------------------------

/** Last watchdog transition per service name. */
export type WatchdogState = LiveSnapshot['watchdog'];
/** Last observed `AiServiceStatus` per service name. */
export type ServiceStatusState = LiveSnapshot['serviceStatus'];
