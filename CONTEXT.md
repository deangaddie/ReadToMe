# Read2Me — Domain Context

Domain vocabulary for ReadToMe. Use these terms exactly in code, tests, and discussion. When a new load-bearing concept is named, add it here.

## Book structure

- **Volume / Part / Chapter / Paragraph / ParagraphItem** — the book hierarchy, top to leaf. A Paragraph holds ordered ParagraphItems. An item is one of: **Character** (spoken dialog needing attribution), **Narration**, or a **Pause** kind.
- **Book Hierarchy** — the in-memory `BookHierarchy` that plans structural mutations (split / merge / title / pause insertion) and returns a `HierarchyMutation` (ToAdd / ToDelete / ToUpdate). The deep planning module; `BookCommandHandler` dispatches commands to it.

## Characters

- **Alias** — an alternate name for a Character (e.g. "Mr. Baggins" for "Bilbo Baggins"). Stored in the `CharacterAliases` table. Aliases are injected into the LLM attribution prompt alongside the canonical name and matched case-insensitively by the queue worker, so an alternate name returned by the LLM resolves to the existing canonical Character instead of creating a duplicate.

## Character attribution

- **Character attribution** — deciding which Character speaks a given Character ParagraphItem, via the LLM. A paragraph with unattributed Character items is **unprocessed**; once a Character is assigned it is **processed**.
- **Character Queue** — the background pipeline. The user adds paragraphs to it; `CharacterQueueWorker` drains it and calls `CharacterAttributionService` per paragraph. `CharacterQueueService` holds queue state (queued / processing / outcome) keyed by `(folder, paragraphId)`.

## Book view

- **Book View Mode** (`BookViewMode { Combined, SplitAttribution, SplitAudio }`) — three mutually exclusive display states for the book tab, selected via a single dropdown toggle. `Combined` shows original paragraphs with attribution checkboxes. `SplitAttribution` shows split ParagraphItems with Character-paragraph checkboxes for the attribution queue. `SplitAudio` shows split ParagraphItems with per-item checkboxes for the audio queue plus play buttons. Replaces the old `bool SplitView`.

## Selection

- **Folder Selection** (`FolderSelection`) — the set of Character paragraphs the user has selected (per project folder) for adding to the Character Queue. The single source of truth is the set of selected paragraph ids plus each one's ancestry `(VolumeId, PartId, ChapterId)`.
- **Roll-up** — a node's tri-state (Unchecked / Indeterminate / Checked) is **derived** from how many of its selectable Character paragraphs are selected versus its total count. Node check-state is never stored; it is computed on read. Selecting a node selects all (or, for **unprocessed-only**, the unprocessed subset of) its Character paragraphs; selection is additive and idempotent.
- **Book Node Level** (`BookNodeLevel { Volume, Part, Chapter }`, in Core) — the selectable triad of the hierarchy. The single enum used by both the reader (to scope Character-paragraph queries) and Folder Selection (to derive roll-up). Replaces the old `SelectionNodeKind`.
- **Selectable node** — a Volume / Part / Chapter containing at least one Character paragraph. Only selectable nodes show a checkbox. Node total counts (`NodeCharacterParagraphCounts`) are seeded on load and held by `FolderSelection`. Selection is always cleared on any structural change (split / merge / reread), so derived state never mixes counts from one structure with selections from another.
- **Audio Item Selection** (`AudioItemSelection`) — the set of ParagraphItems (Character or Narration only; Pause excluded) the user has selected for adding to the Audio Queue. Parallel to `FolderSelection` but selects at ParagraphItem granularity, not Paragraph. Rolls up to Chapter/Part/Volume nodes for tri-state checkboxes. Cleared on view mode switch or any structural change (split / merge / reread).
- **Generatable item** — a non-Pause ParagraphItem that can actually have audio generated: it is **missing audio** (no WAV) **and** **attribution-ready** (a Narration item always qualifies; a Character item qualifies only once it has a Character assigned). The audio analog of an **unprocessed** attribution paragraph. The node-level "select needs audio" control (`needsAudioOnly`) selects only Generatable items beneath a node; the per-item checkbox for an unattributed Character item is disabled because it is not yet Generatable. A node's audio roll-up denominator still counts *all* non-Pause items, so a needs-audio select lands on Indeterminate, never Checked — same as "select unprocessed" for attribution.

## Node status

- **Node Status Badge** — a per-node (Volume / Part / Chapter) summary count of remaining work shown in the expansion-panel header. Three **stages**: **attribution remaining** (paragraphs with an unattributed Character item), **audio remaining** (paragraphs with a non-Pause item lacking a WAV — counted regardless of attribution), and **review** (paragraphs with an `AudioReview` row in `NeedsReview` state; `Dismissed` excluded). Counts are **normalised to Paragraph granularity** — a paragraph qualifies for a stage if *any* of its items qualify, contributing 1 regardless of item count. A node with all three stages at zero shows a single "done" indicator instead of empty badges. Shown in every Book View Mode.
- **Node Status roll-up** — each node's badge is the **cumulative** count of qualifying paragraphs beneath it (Volume ≥ Part ≥ Chapter); the same paragraph echoes up every ancestor. Held by the singleton `NodeStatusService`, which stores per-paragraph stage **counters** (`unattributed`, `missingAudio`, `review`) plus `(Chapter, Part, Volume)` ancestry. Seeded on load via `IProjectReader.GetNodeStatusSeedAsync` and updated **live**: the `BookHierarchyPresenter` pushes `RecomputeParagraph` deltas after each attribution / audio-assigned / review change. **Cleared and reseeded** on any structural change (split / merge / reread), never patched incrementally — same discipline as `FolderSelection`.

## Audio

- **ParagraphItem Audio** — a generated audio file associated with a single ParagraphItem. Visible in `SplitAudio` view only. A ParagraphItem row shows a play button when an audio file exists; tapping it opens an inline player. No audio file → no play button.
- **Audio Queue** — the background pipeline that generates audio for selected ParagraphItems. The user adds items via `AudioItemSelection`; the queue processes them one at a time using the Character's voice (for Character items) or the narrator voice (for Narration items).
- **Audio Gen Stream** — the live per-item progression shown in the expandable `AudioQueueStatusBar`. `AudioQueueProcessor` publishes typed `AudioGenEvent`s to the singleton `AudioGenBroadcaster`; the expandable view accumulates one card per processed item and tail-scrolls to the newest. Mirrors the LLM `LlmStreamBroadcaster` / `LlmStreamView` pattern. Each card shows the line Character, source text, and four pipeline **phases** — audio generation, normalize, Whisper transcribe, verify — each rendered Pending (spinner) → Done/Fail as its event arrives.
- **Phase fail vs Item failure** — a **phase fail** (normalize ✗ or verify ✗) means the audio was still stored and the item completed; it surfaces as an `AudioReview`. An **Item failure** (`AudioGenEvent` Failed) is a hard stop with no audio (row missing, no character, no voice, no TTS config, or exception). The Audio Gen Stream card renders these distinctly: a phase ✗ on one row vs a terminal red error.

## Sentence chunking

- **Sentence Chunking** — splitting a ParagraphItem's text into individual sentences, synthesising each separately, then stitching the per-sentence audio back into one item-level WAV. Long text sent to a TTS service in one call can peak / produce artifacts; per-sentence calls avoid it. Applied transparently inside the TTS layer — the rest of the audio pipeline (normalize, store one `{id}.wav`, Whisper verify against the full source text) is unchanged. Verify still scores the whole stitched item, not per sentence.
- **`SentenceChunkedTtsClient`** — the decorator over `IParagraphTtsClient` that owns chunking. It **self-gates**: when `SentenceSplitEnabled` is off it passes the text straight to the inner client. Wired in by `ParagraphTtsClientResolver`, so every TTS service type is wrapped and `AudioQueueProcessor` stays unaware of chunking. One chunk (or a short item) stitches to a byte-identical passthrough.
- **Sentence Pause** (`SentencePauseMs`) — silence inserted **between** adjacent sentences when stitching (N chunks → N−1 gaps; no leading/trailing silence, so inter-item spacing is unchanged). Zero-PCM samples spliced into the WAV `data`.
- **Min chunk** (`SentenceMinChunkChars`) — a sentence fragment shorter than this is merged into its neighbour, so titles / short exclamations ("Mr.", "Yes.") don't become their own micro-chunk. Internal tuning knob — stored but not surfaced in the UI.
- **Chunk failure** — if any one sentence's TTS call throws, the whole item fails (the decorator throws; the processor's existing catch marks it an Item failure). No partial / gappy audio is ever stored.
