# impl-04 — Add Llama model preparation and streamed completion

Type: implementation
Status: resolved
Blocked by: impl-03
Spec: [../spec.md](../spec.md) — Llama router capability and shared run lifecycle

## Goal

Deliver the Llama detail page end to end: discover router presets, select/switch by request,
stream thinking and answer independently, settle only on a valid terminal event, and
refresh model residency without leaking SSE quirks into shared UI code.

## Scope

- Add Llama's preparation state/controller/epoch, `/v1/models` validation, status-labelled
  options, locked selection/preservation/fallback rules, retryable diagnostics, and Run
  disablement until prepared.
- Add prompt/model common fields, visible Advanced defaults, validated Additional request
  properties, reserved-key rejection, final request diagnostics, and forced one-user-message
  `stream:true` completion request.
- Implement a DOM-free incremental SSE parser across arbitrary byte/chunk and UTF-8
  boundaries, with LF/CRLF, comments, blanks, metadata-only deltas, reasoning fallback,
  answer deltas, finish/usage/timing metadata, `[DONE]`, error envelopes, and abort.
- Emit live thinking/answer progress, render separated aggregated result content, keep
  incomplete partial output diagnostic-only, and refresh model status after settlement.

## Acceptance

- [x] Unit tests cover preparation payload/status/selection cases and every representative
  SSE split point, multi-byte UTF-8, both newline styles, metadata, both reasoning keys,
  answer, terminal metadata, malformed envelope/JSON, missing `[DONE]`, premature EOF,
  error response, and abort.
- [x] Real-proxy tests prove model discovery, selected-model autoload request shape, arbitrary
  chunk streaming, immediate visible deltas, cancellation in both race orders, target
  disconnect, and bounded final/incomplete diagnostics.
- [x] Browser tests prove open/manual-check/post-run preparation, preserved/fallback selection,
  disabled/retry UI on preparation failure, streamed thinking/answer, elapsed time,
  duplicate prevention, terminal success, model refresh, and no late mutation.
- [x] Llama router readiness remains separate from preset residency; there is no stale
  `POST /v1/models` or unverified free-text model fallback.
- [x] New prepared/running/streamed/result/error states pass axe and the full deterministic
  gate remains green.

## Implementation notes

- Added page-owned, abortable model preparation with epoch rejection, validated router
  status options, selection preservation/fallback, retry diagnostics, and independent
  readiness/preparation refresh behavior.
- Added the configured Llama Service Adapter with exact visible defaults, reserved-property
  validation, forced one-message streaming requests, bounded request/stream diagnostics,
  and normalized LLM results/failures.
- Added a DOM-free incremental SSE parser that handles arbitrary byte and UTF-8 splits,
  LF/CRLF, comments, metadata-only events, both reasoning keys, answer deltas, terminal
  metadata, immediate `[DONE]` settlement, abort, malformed/incomplete streams, and bounded
  diagnostic summaries.
- Added live separated thinking/answer presentation, prepared/running/result/error axe
  coverage, and real-proxy preparation, request, streaming, cancellation, and refresh
  coverage. The deterministic gate passes 38 unit, 22 Chromium, and 13 accessibility tests.
