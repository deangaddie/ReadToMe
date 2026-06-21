# Audio Gen Stream — expandable live view in the Audio Queue status bar

Labels: `ready-for-agent`

## Problem Statement

When the user generates audio for ParagraphItems, the Audio Queue status bar shows only aggregate progress (queued count, elapsed, ETA). The user cannot see what is happening to each item as it is generated: which Character's line is being voiced, the source text, what Whisper transcribed it back to, whether the WER verification passed, or whether loudness normalization succeeded. Today that detail only surfaces *after the fact*, as an `AudioReview` warning chip on a failed row — there is no live, per-item picture of the pipeline as it runs.

The Character attribution side already solves this: the `CharacterQueueStatusBar` expands to reveal `LlmStreamView`, a live stream of each LLM request/response. The Audio side has no equivalent.

## Solution

The Audio Queue status bar gains an expandable bottom panel, identical in feel to the Character attribution one. When expanded it shows a live, accumulating stream — one card per processed ParagraphItem — that fills in as the generation pipeline advances:

- the line **Character** (or Narrator) and the **source text** being generated,
- the four pipeline phases — audio generation, loudness normalize, Whisper transcribe, verify — each shown as a spinner while pending and resolving to ✓ / ✗,
- the returned **Whisper transcript**, stacked beneath the source text so the two can be compared by eye,
- the **normalize result** (success / fail, with reason) and the **verify result** (pass / fail, with the WER number).

As each item progresses, its card updates in place; when the item finishes, the next item's card appears below and the view tail-scrolls to keep the active card in view. The history of completed cards remains scrollable for the life of the panel.

## User Stories

1. As an audiobook producer, I want to expand the Audio Queue status bar, so that I can watch audio generation happen live instead of only seeing aggregate counts.
2. As a producer, I want each item's card to name the Character (or Narrator) whose line is being voiced, so that I know whose voice is in play.
3. As a producer, I want to see the exact source text being generated for the current item, so that I can confirm the right line is being processed.
4. As a producer, I want a spinner shown on each pipeline phase while it is running, so that I can see *what is happening right now* (generating audio vs transcribing vs verifying).
5. As a producer, I want each phase to resolve to a clear ✓ or ✗ when it completes, so that I can tell at a glance which phases succeeded.
6. As a producer, I want to see the Whisper transcript returned for the generated audio, so that I can judge whether the audio sounds like the intended text.
7. As a producer, I want the source text and the transcript shown as stacked labeled rows, so that I can compare them directly.
8. As a producer, I want to see the verify result with the WER number, so that I understand *why* an item passed or failed verification.
9. As a producer, I want to see the normalize result (success/fail with reason), so that I know whether loudness normalization was applied.
10. As a producer, I want completed item cards to remain in the panel, so that I can scroll back to review earlier items in the run.
11. As a producer, I want the panel to auto-scroll to the active card as work progresses, so that I do not have to manually scroll to follow the live item.
12. As a producer, I want items that fail hard (no audio produced) to show a distinct terminal error, so that I can tell a hard failure apart from a phase that merely flagged for review.
13. As a producer, I want a card to appear even when an item fails before its Character is resolved (e.g. the row is missing), so that no processed item disappears silently.
14. As a producer, I want the panel to show audio generated across any project, so that background work is visible regardless of which project I am currently viewing.
15. As a producer, I want cancelling the queue to stop work but keep the stream history visible, so that I can still read what already failed before I cancelled.
16. As a producer, I want to collapse the panel back to the slim status bar, so that it does not occupy the screen when I am not watching it.
17. As a producer generating audio for a Narration item, I want the card to show "Narrator" as the speaker, so that narration and dialog are distinguishable in the stream.

## Implementation Decisions

### New broadcaster (mirrors the LLM stream pattern)

- A new singleton `AudioGenBroadcaster` is introduced in the `Read2Me.Services` Audio area, modelled exactly on the existing `LlmStreamBroadcaster`: an `event Action<AudioGenEvent>` plus a `Publish` method. Registered as a singleton alongside `LlmStreamBroadcaster`.
- A typed `AudioGenEvent` record hierarchy carries the per-item progression. Each event identifies its ParagraphItem by id:
  - `ItemStarted(id, character, text)` — character/text may be null when not yet resolved.
  - `AudioGenerated(id)` — TTS produced audio.
  - `Normalized(id, ok, reason)` — loudness normalize phase outcome.
  - `Transcribed(id, transcript)` — Whisper returned a transcript.
  - `Verified(id, ok, wer, reason)` — WER comparison outcome.
  - `Failed(id, reason)` — hard stop, no audio produced.

### Processor publishes at phase boundaries

- `AudioQueueProcessor` takes `AudioGenBroadcaster` as a new constructor dependency and publishes an event at each phase boundary of `ProcessItemAsync`.
- `ItemStarted` is published **first**, before any work, using the queued item's ParagraphItem id. This guarantees every processed item produces a visible card — including items that fail before the Character/text are known (e.g. the row is not found). The character/text fields are populated once resolved.
- The existing persistence and queue-state behavior is unchanged. Publishing is purely additive: the broadcaster does not alter the `AudioReview` write, the in-memory `AudioReviewService` mirror, or the `AudioQueueService` outcome/complete signalling.

### Phase fail vs item failure

These two concepts (already added to the domain glossary) are rendered distinctly:

- A **phase fail** — normalize ✗ or verify ✗ — means audio was still stored and the item completed; it corresponds to the existing `AudioReview` upsert. In the card it is a ✗ on that phase's row.
- An **item failure** — `Failed` event — is a hard stop with no audio (row missing, no Character, no voice, no TTS config, or an exception). In the card it is a terminal red error, not a phase ✗.

### New view component

- A new `AudioGenStreamView` Razor component, modelled on `LlmStreamView`, subscribes to `AudioGenBroadcaster.Event`, accumulates one card per item keyed by ParagraphItem id, and updates the matching card as later events for that id arrive.
- No project/folder filtering — the view shows all items, matching `LlmStreamView`. (The queue is global and serial, so only one item is in flight at a time.)
- Cards accumulate without a cap for the component's lifetime. Newest card at the bottom.
- Each card shows: Character/Narrator + source text header; four phase rows (audio generation, normalize, Whisper transcribe, verify) each Pending(spinner) → ✓/✗; the transcript stacked beneath the source text as labeled rows; final normalize result (✓/✗ + reason) and verify result (✓/✗ + WER number). A `Failed` event renders as a terminal red error in place of further phase rows.
- **Tail-scroll** is net-new behavior: after each update the view scrolls its container to the bottom (a small JS interop call, `scrollTop = scrollHeight`). `LlmStreamView` has a scroll-container ref but does not currently auto-scroll; this view does.

### Status bar wiring

- `AudioQueueStatusBar` gains an `_expanded` toggle, a 50vh bottom panel, and a backdrop scrim — copied from `CharacterQueueStatusBar`'s expand mechanism. When expanded it renders `AudioGenStreamView`.
- `CancelAll` is unchanged: cancelling stops queue work but does not clear the stream view's accumulated history.

## Testing Decisions

- **What makes a good test here:** assert *external behavior* observable at the processor seam — the sequence and payloads of `AudioGenEvent`s published for a given scenario — not the internal field-filling of the view. The Razor view is a dumb event accumulator; all decision-bearing logic lives in the processor.
- **Single seam:** `AudioQueueProcessor.ProcessItemAsync`. The broadcaster is added as a constructor dependency; tests pass a capturing test double (or subscribe to a real `AudioGenBroadcaster`) and assert the published event sequence after driving `ProcessItemAsync`.
- **Module under test:** `AudioQueueProcessor`.
- **Prior art:** `AudioQueueProcessorTests` already constructs the processor directly with fakes (`FakeTtsClient`, `FakeNormalizer`, `FakeWerComparer`, `FakeTranscriptionClient`, etc.) and asserts post-conditions per scenario. New tests extend the same fixture with one more dependency and assert on captured events. Scenarios to cover, reusing the existing seed helpers:
  - Happy path: `ItemStarted` (with Character + text) → `AudioGenerated` → `Normalized(ok)` → `Transcribed` → `Verified(ok)`, no `Failed`.
  - Phase fail (WER over threshold): `Verified(ok: false, wer)` published, no `Failed`.
  - Phase fail (normalize skipped): `Normalized(ok: false, reason)` published, no `Failed`.
  - Hard fail with Character known (no default voice): `ItemStarted` then `Failed`, no later phase events.
  - Hard fail before Character resolved (row not found): `ItemStarted` (null character/text) then `Failed`.
  - Narration item: `ItemStarted` reports the Narrator as speaker.
- **Not tested (matches repo precedent):** the Razor components (`AudioGenStreamView`, expandable `AudioQueueStatusBar`) and the JS tail-scroll. The existing `LlmStreamView` and `CharacterQueueStatusBar` have no component tests.

## Out of Scope

- Persisting the live stream history across page reloads or between sessions (it lives only for the component's lifetime, like `LlmStreamView`).
- Per-project/folder filtering of the stream.
- Capping or trimming card history.
- Re-queueing or retrying a failed item from within the stream view (review/dismiss continues to flow through the existing `AudioReviewChip`).
- Changing any persistence, the `AudioReview` schema, the WER/normalize logic, or the queue/outcome state machine.
- Side-by-side (two-column) transcript comparison — stacked rows only.

## Further Notes

- Domain terms **Audio Gen Stream** and **Phase fail vs Item failure** have been added to `CONTEXT.md`; the entry for **Audio Queue** was updated to note the pipeline is now built.
- The design deliberately mirrors the proven Character-attribution stream stack (`LlmStreamBroadcaster` → `LlmStreamView`, expandable `CharacterQueueStatusBar`) so the two status bars stay visually and structurally consistent.
- Wiring note (precedent, not a hard requirement): the processor is registered scoped and the broadcaster singleton — the same lifetime split that already works for the LLM stream.
