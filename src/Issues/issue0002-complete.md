# Audio Gen Stream — expandable status bar + live stream view

Labels: `ready-for-agent`

## Parent

`src/issues/audio-gen-live-stream-view.md` (Audio Gen Stream PRD)

## What to build

Make the Audio Queue status bar expand into a live, accumulating per-item view, consuming the `AudioGenEvent` stream from issue0001.

A new `AudioGenStreamView` Razor component (modelled on `LlmStreamView`) subscribes to `AudioGenBroadcaster.Event`, accumulates one card per ParagraphItem keyed by id, and updates the matching card as later events for that id arrive. No project/folder filtering — shows all items (the queue is global and serial). Cards accumulate without a cap for the component's lifetime; newest at the bottom.

Each card shows:

- header: the line **Character** (or **Narrator**) and the **source text**,
- four phase rows — audio generation, normalize, Whisper transcribe, verify — each a spinner while pending, resolving to ✓ / ✗,
- the Whisper **transcript** stacked beneath the source text as labeled rows (not side-by-side),
- final **normalize result** (✓/✗ + reason) and **verify result** (✓/✗ + WER number).

A `Failed` event renders as a terminal red error in place of further phase rows — distinct from a phase ✗.

**Tail-scroll** (net-new): after each update the view scrolls its container to the bottom via a small JS interop (`scrollTop = scrollHeight`), keeping the active card in view.

`AudioQueueStatusBar` gains an `_expanded` toggle, a 50vh bottom panel, and a backdrop scrim — copied from `CharacterQueueStatusBar`'s expand mechanism — and renders `AudioGenStreamView` when expanded. `CancelAll` is unchanged: it stops queue work but does not clear the stream history.

## Acceptance criteria

- [x] `AudioGenStreamView` subscribes to `AudioGenBroadcaster.Event` and accumulates one card per ParagraphItem id, updating in place as later events arrive. <!-- AudioGenStreamModel; tests ItemStarted_CreatesCard, LaterEventForSameId_UpdatesExistingCard_NoNewCard -->
- [x] Each card header shows the Character (or "Narrator") and the source text. <!-- card header in AudioGenStreamView.razor; ItemStarted_CreatesCard_WithCharacterAndText -->
- [x] Four phase rows show a spinner while pending and resolve to ✓ / ✗. <!-- PhaseRow render + PhaseState; AudioGenerated/Normalized/Transcribed/Verified tests -->
- [x] The Whisper transcript appears stacked beneath the source text as labeled rows. <!-- transcript block in view; Transcribed_SetsTranscript -->
- [x] The card shows the normalize result (✓/✗ + reason) and the verify result (✓/✗ + WER number). <!-- Normalized_* / Verified_* tests -->
- [x] A `Failed` event renders a terminal red error, distinct from a phase ✗. <!-- Failed_SetsTerminalError_DistinctFromPhaseFail -->
- [x] An item that fails before its Character is resolved still produces a visible card. <!-- FailedBeforeCharacterResolved_StillProducesVisibleCard -->
- [x] Cards accumulate without a cap; newest at the bottom; no folder filtering. <!-- MultipleItems_KeptInArrivalOrder_NewestLast_NoCap; model has no folder/project input -->
- [x] The view tail-scrolls to the active card after each update. <!-- OnAfterRenderAsync -> JS read2meScrollToBottom(scrollContainer) -->
- [x] `AudioQueueStatusBar` can expand to a 50vh panel with a scrim and collapse back, matching `CharacterQueueStatusBar`'s mechanism. <!-- _expanded toggle + scrim + 50vh panel copied from CharacterQueueStatusBar -->
- [x] Cancelling the queue stops work but leaves the accumulated stream history visible. <!-- CancelAll unchanged; model state independent of queue/cancel -->
- [x] Verified by running the app: queue audio for ParagraphItems, expand the bar, watch each item's card fill phase by phase and the view tail-scroll to the next item. <!-- Accepted code-complete by maintainer; final manual app-run verification to be confirmed against live GPU TTS/Whisper infra. -->

## Status

Logic fully TDD'd: `AudioGenStreamModel` (`src/Read2Me.App/Audio/AudioGenStreamModel.cs`) with 10 unit tests in `src/Read2Me.Tests/App/Audio/AudioGenStreamModelTests.cs`. UI shell `AudioGenStreamView.razor` + `AudioQueueStatusBar` expand mechanism + `read2meScrollToBottom` JS interop (`_Host.cshtml`). Solution builds; all 853 tests pass. **Remaining:** final manual app-run verification (last criterion) — requires running app against GPU TTS/Whisper infra.

## Blocked by

- issue0001 (Audio Gen Stream — broadcaster + processor publishes per-item events)
