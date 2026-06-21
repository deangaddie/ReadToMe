# Audio Gen Stream — broadcaster + processor publishes per-item events

Labels: `ready-for-agent`

## Parent

`src/issues/audio-gen-live-stream-view.md` (Audio Gen Stream PRD)

## What to build

Introduce the live event channel behind the Audio Gen Stream, end-to-end from service to processor, verifiable by tests alone.

A new singleton `AudioGenBroadcaster` (modelled exactly on `LlmStreamBroadcaster`: an `event Action<AudioGenEvent>` plus a `Publish` method) lives in the `Read2Me.Services` Audio area and is registered as a singleton alongside `LlmStreamBroadcaster`.

A typed `AudioGenEvent` record hierarchy carries the per-item progression, each event keyed by ParagraphItem id:

- `ItemStarted(id, character, text)` — character/text may be null when not yet resolved
- `AudioGenerated(id)` — TTS produced audio
- `Normalized(id, ok, reason)` — loudness normalize outcome
- `Transcribed(id, transcript)` — Whisper returned a transcript
- `Verified(id, ok, wer, reason)` — WER comparison outcome
- `Failed(id, reason)` — hard stop, no audio produced

`AudioQueueProcessor` takes `AudioGenBroadcaster` as a new constructor dependency and publishes an event at each phase boundary of `ProcessItemAsync`. `ItemStarted` is published **first**, before any work, using the queued item's ParagraphItem id — so every processed item produces an event even when it fails before the Character/text are known (e.g. row not found). Character/text fields fill once resolved.

Publishing is purely additive. The `AudioReview` write, the in-memory `AudioReviewService` mirror, and the `AudioQueueService` outcome/complete signalling are all unchanged. A **phase fail** (normalize ✗ / verify ✗) still stores audio and publishes `Normalized`/`Verified` with `ok: false`; an **item failure** publishes `Failed` with no audio.

## Acceptance criteria

- [x] `AudioGenBroadcaster` exists in the Services Audio area, mirrors `LlmStreamBroadcaster`'s shape, and is registered as a singleton in DI.
- [x] `AudioGenEvent` record hierarchy exists with the six event types above.
- [x] `AudioQueueProcessor` publishes `ItemStarted` first, before any other work, using the queued item's ParagraphItem id.
- [x] Happy path publishes, in order: `ItemStarted` (with Character + text) → `AudioGenerated` → `Normalized(ok: true)` → `Transcribed` → `Verified(ok: true)`, and no `Failed`.
- [x] WER over threshold publishes `Verified(ok: false)` carrying the WER number, and no `Failed`.
- [x] Normalize skipped publishes `Normalized(ok: false)` with the reason, and no `Failed`.
- [x] Hard fail with Character known (no default voice) publishes `ItemStarted` then `Failed`, with no later phase events.
- [x] Hard fail before Character resolved (row not found) publishes `ItemStarted` (null character/text) then `Failed`.
- [x] A Narration item's `ItemStarted` reports the Narrator as speaker.
- [x] Existing `AudioQueueProcessorTests` assertions (persistence, queue outcome, review row) still pass — publishing changes nothing else.
- [x] New tests extend the existing `AudioQueueProcessorTests` fixture with a capturing broadcaster double and assert the published event sequence per scenario.

## Blocked by

None - can start immediately.
