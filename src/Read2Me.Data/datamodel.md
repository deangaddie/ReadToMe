# Data model

## Ordering

The book tables (Volume, Part, Chapter, Paragraph, ParagraphItem) order by fractional indexing via the `FractionalIndexing` NuGet package, and VoiceRule orders by its `Rank` the same way. These keys are Base-62 (`a-zA-Z0-9`), BINARY-collated (case-sensitive). Every row has a stable `Guid Id` PK except StepCheckpoint, which is keyed by its step.

Every book keeps at least one Volume. Parts and Chapters may be absent: a new Volume or Part is born empty (Structure rules, `StructureRules`). An import always writes at least one Part and Chapter, and singular nodes are hidden in the UI.

## Tables

### Project

| Column                | Type   | Nullable | Notes                                                              |
| --------------------- | ------ | -------- | ------------------------------------------------------------------ |
| `Id`                  | Guid   | no       | PK                                                                 |
| `Title`               | string | no       | max 250 — project label                                            |
| `BookTitle`           | string | no       | max 250                                                            |
| `Author`              | string | no       | max 250                                                            |
| `Filename`            | string | no       | max 526                                                            |
| `Type`                | enum   | no       | `BookFileType` (Epub / Text)                                       |
| `CoverImage`          | string | yes      | file name of the uploaded cover (jpg, jpeg, png or webp) in the project folder                                                  |
| `NarratorOnlyMode`    | bool   | no       | suppress character voices                                          |
| `NarratorCharacterId` | Guid   | yes      | narrator link — no FK; read only via `NarratorIdentity` (ADR-0004) |

### Volume

| Column  | Type   | Nullable | Notes          |
| ------- | ------ | -------- | -------------- |
| `Id`    | Guid   | no       | PK             |
| `Title` | string | no       | max 250        |
| `Order` | string | no       | fractional key |

### Part

| Column     | Type   | Nullable | Notes          |
| ---------- | ------ | -------- | -------------- |
| `Id`       | Guid   | no       | PK             |
| `VolumeId` | Guid   | no       | FK → Volume    |
| `Title`    | string | yes      | max 250        |
| `Order`    | string | no       | fractional key |

### Chapter

| Column   | Type   | Nullable | Notes          |
| -------- | ------ | -------- | -------------- |
| `Id`     | Guid   | no       | PK             |
| `PartId` | Guid   | no       | FK → Part      |
| `Title`  | string | yes      | max 250        |
| `Order`  | string | no       | fractional key |

### Paragraph

A paragraph has no speaker of its own — the speaker(s) are derived from its Items, which may
each carry a different Character.

| Column        | Type   | Nullable | Notes                                                             |
| ------------- | ------ | -------- | ----------------------------------------------------------------- |
| `Id`          | Guid   | no       | PK                                                                |
| `ChapterId`   | Guid   | no       | FK → Chapter                                                      |
| `Order`       | string | no       | fractional key                                                    |

### ParagraphItem

Items within a paragraph. A paragraph with no mixed content is one item. Mixing narration and dialog splits the paragraph into multiple items.

The split is made once, at import, by `ParagraphSplitter`, and only a user may change it afterwards: attribution stamps `CharacterId` / `VoiceInstructions` on existing rows and never inserts, deletes or rewrites one ([ADR 0005](../../docs/adr/0005-frozen-paragraph-item-boundaries.md)).

| Column              | Type   | Nullable | Notes                                                                                                      |
| ------------------- | ------ | -------- | ---------------------------------------------------------------------------------------------------------- |
| `Id`                | Guid   | no       | PK                                                                                                         |
| `ParagraphId`       | Guid   | no       | FK → Paragraph                                                                                             |
| `Order`             | string | no       | fractional key                                                                                             |
| `ItemType`          | enum   | no       | `ParagraphItemType` — Speech, VolumePause, PartPause, ChapterPause, ParagraphPause, Pause; stored as text. Narration vs dialog comes from `CharacterId` (ADR 0006)  |
| `Text`              | string | yes      | spoken / narration text                                                                                    |
| `CharacterId`       | Guid   | yes      | FK → Character — what the item is: null = unattributed dialog, Narrator sentinel = narration, else that character's line |
| `VoiceInstructions` | string | yes      | max 3000, free-text delivery cue                                                                           |
| `AudioFileName`     | string | yes      | relative path to generated WAV, max 512                                                                    |

### Character

| Column       | Type   | Nullable | Notes                                      |
| ------------ | ------ | -------- | ------------------------------------------ |
| `Id`         | Guid   | no       | PK                                         |
| `Name`       | string | no       | max 250                                    |
| `IsNarrator` | bool   | no       | true for the special per-project Narrator  |

### CharacterAlias

Alternate names for a Character. Matched case-insensitively by the attribution queue worker so LLM responses using aliases resolve to the canonical Character.

| Column        | Type   | Nullable | Notes          |
| ------------- | ------ | -------- | -------------- |
| `Id`          | Guid   | no       | PK             |
| `CharacterId` | Guid   | no       | FK → Character |
| `Name`        | string | no       | max 250; unique per (CharacterId, Name) |

### Pronunciation

One entry in the book's Pronunciation list: TTS is asked to say `SpokenForm` wherever an item's text has `WrittenForm` (whole words, any case). No ordering column: the list sorts by written form, and where two overlap the longer wins. Written forms are unique ignoring case, enforced on write.

| Column        | Type   | Nullable | Notes   |
| ------------- | ------ | -------- | ------- |
| `Id`          | Guid   | no       | PK      |
| `WrittenForm` | string | no       | max 100 |
| `SpokenForm`  | string | no       | max 200 |

### Voice

| Column                            | Type     | Nullable | Notes                                        |
| --------------------------------- | -------- | -------- | -------------------------------------------- |
| `Id`                              | Guid     | no       | PK                                           |
| `CharacterId`                     | Guid     | no       | FK → Character                               |
| `Name`                            | string   | no       | max 250                                      |
| `Description`                     | string   | yes      | max 1000                                     |
| `Source`                          | enum     | no       | `VoiceSource` (Uploaded / Generated) |
| `DesignPrompt`                    | string   | yes      | text description used for voice design, max 4000 |
| `Transcript`                      | string   | yes      | transcript of reference audio (cloning), max 4000 |
| `AudioFileName`                   | string   | yes      | relative path to reference WAV, max 512     |
| `VoiceDesignSettingsOverrideJson` | string   | yes      | per-voice TTS design settings override, max 4000 |
| `TtsSettingsOverrideJson`         | string   | yes      | per-voice TTS generation settings override, max 4000 |
| `CreatedUtc`                      | DateTime | no       |                                              |

### VoiceRule

Ordered per-Character rules that select which Voice to use over a position range. See `context/voice-rules.md` for full evaluation semantics.

| Column        | Type             | Nullable | Notes                                             |
| ------------- | ---------------- | -------- | ------------------------------------------------- |
| `Id`          | Guid             | no       | PK                                                |
| `CharacterId` | Guid             | no       | FK → Character                                    |
| `VoiceId`     | Guid             | no       | FK → Voice                                        |
| `Rank`        | string           | no       | BINARY fractional key; rules run in ascending order and the last passing one wins |
| `IsDefault`   | bool             | no       | exactly one default rule per Character            |
| `FromLevel`   | VoiceAnchorLevel | yes      | anchor level for start bound                      |
| `FromNodeId`  | Guid             | yes      | node id for start bound                           |
| `ToLevel`     | VoiceAnchorLevel | yes      | anchor level for end bound                        |
| `ToNodeId`    | Guid             | yes      | node id for end bound                             |

### AudioReview

One row per ParagraphItem that has a completed audio generation attempt with a non-trivial outcome (normalize or verify issue). `Dismissed` state = user reviewed and accepted.

| Column                  | Type             | Nullable | Notes                                          |
| ----------------------- | ---------------- | -------- | ---------------------------------------------- |
| `Id`                    | Guid             | no       | PK                                             |
| `ParagraphItemId`       | Guid             | no       | FK → ParagraphItem (unique)                    |
| `State`                 | AudioReviewState | no       | NeedsReview / Dismissed                        |
| `NormalizeOk`           | bool             | no       |                                                |
| `NormalizeReason`       | string           | yes      | max 500                                        |
| `VerifyOk`              | bool             | no       |                                                |
| `Wer`                   | double           | yes      | word error rate                                |
| `VerifyReason`          | string           | yes      | max 500                                        |
| `Transcript`            | string           | yes      | Whisper transcript, max 8000                   |
| `OriginalTextSnapshot`  | string           | yes      | source text at generation time, max 8000       |
| `CreatedUtc`            | DateTime         | no       |                                                |
| `UpdatedUtc`            | DateTime         | no       |                                                |

### StepCheckpoint

A Pipeline step the user confirmed or skipped (one project per database, so the step alone is the key). A Book mutation can mark it stale; a reread deletes every row.

| Column  | Type            | Nullable | Notes                                                  |
| ------- | --------------- | -------- | ------------------------------------------------------ |
| `Step`  | PipelineStep    | no       | PK, stored as the enum name (`Import` … `Export`)       |
| `State` | CheckpointState | no       | Confirmed / Skipped, stored as text                    |
| `Stale` | bool            | no       | a later mutation disturbed what was confirmed          |
| `At`    | DateTimeOffset  | no       | when it was confirmed or skipped; kept when it goes stale |

## Other databases

The tables above are one book's `project.db`. Two more per-project or per-registry stores sit beside it; neither is part of Project state.

### History (`{project}/history/history.db`)

Raw SQLite, created by `HistoryDatabase` (not EF), versioned by `PRAGMA user_version`. Snapshots, blobs and a throwaway cache sit in sibling folders ([ADR 0016](../../docs/adr/0016-project-history-snapshots-and-session-patchsets.md)).

| Table   | Columns                                                                                                                                                              |
| ------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Step`  | `StepId` (PK), `ParentStepId`, `Sequence` (unique), `Label`, `Author`, `StartedAt`, `EndedAt`, `IsOpen`, `RestoredFromStepId`, `Note`, `Summary`, `CantOpen`, `Kind`, `Action` |
| `Head`  | `Id` (always 1), `StepId` — the tip                                                                                                                                  |
| `Chunk` | `StepId`, `Sequence` (PK together), `Patchset` (SQLite session changeset), `FileChanges`                                                                              |

### Series registry (`{workspace}/.series/{seriesId}/series.db`)

One database per Series, EF Core (`SeriesDbContext`, own migrations in `Series/Migrations`), no History ([ADR 0018](../../docs/adr/0018-replicated-series-voices.md)). Books are keyed by their project's `Id`, never by folder name. Membership and links live only here, not in any `project.db`.

| Table              | Columns                                                                                          | Notes                                                                                           |
| ------------------ | ------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------- |
| `Series`           | `Id` (PK), `Name` (max 250)                                                                      | the registry's single row                                                                       |
| `Books`            | `ProjectId` (PK), `Position` (decimal, stored as text)                                           | a member book and its Series position                                                           |
| `SeriesCharacters` | `Id` (PK)                                                                                        | identity only; label is derived from the linked Characters, Voices live in each book            |
| `Links`            | `SeriesCharacterId`, `ProjectId` (PK together), `CharacterId`                                    | FK → SeriesCharacters (cascade); unique on (`ProjectId`, `CharacterId`)                         |
| `NotTheSame`       | `Id` (PK), `ProjectId`, `CharacterId`, `SeriesCharacterId?`, `OtherProjectId?`, `OtherCharacterId?` | a dismissed link suggestion: a cell (`SeriesCharacterId` set) or one pair of a row (`Other…` set); FK → SeriesCharacters (cascade) |

### App settings (`app.db`)

Workspace-wide LLM, TTS, voice-design, transcription and similarity configs, prompts, audio-processing settings and themes (`Read2Me.AppData`). Not per book.
