# Data model

## Ordering

All tables use fractional indexing for ordering via the `FractionalIndexing` NuGet package. `Order` columns are Base-62 (`a-zA-Z0-9`), BINARY-collated (case-sensitive). All rows have a stable `Guid Id` PK.

Every book has at least one Volume, Part, and Chapter. Singular nodes are hidden in the UI but always present in the data.

## Tables

### Project

| Column              | Type   | Nullable | Notes                              |
| ------------------- | ------ | -------- | ---------------------------------- |
| `Id`                | Guid   | no       | PK                                 |
| `Title`             | string | no       | max 250 — project label            |
| `BookTitle`         | string | no       | max 250                            |
| `Author`            | string | no       | max 250                            |
| `Filename`          | string | no       | max 526                            |
| `Type`              | enum   | no       | `BookFileType` (Epub / Text)       |
| `CoverImage`        | string | yes      | relative jpg path                  |
| `NarratorOnlyMode`  | bool   | no       | suppress character voices          |

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

| Column        | Type   | Nullable | Notes                                                             |
| ------------- | ------ | -------- | ----------------------------------------------------------------- |
| `Id`          | Guid   | no       | PK                                                                |
| `ChapterId`   | Guid   | no       | FK → Chapter                                                      |
| `Order`       | string | no       | fractional key                                                    |
| `CharacterId` | Guid   | yes      | FK → Character — set when paragraph is a single-character item    |

### ParagraphItem

Items within a paragraph. A paragraph with no mixed content is one item. Mixing narration and dialog splits the paragraph into multiple items.

| Column              | Type   | Nullable | Notes                                                                                                      |
| ------------------- | ------ | -------- | ---------------------------------------------------------------------------------------------------------- |
| `Id`                | Guid   | no       | PK                                                                                                         |
| `ParagraphId`       | Guid   | no       | FK → Paragraph                                                                                             |
| `Order`             | string | no       | fractional key                                                                                             |
| `ItemType`          | enum   | no       | `ParagraphItemType` — Narration, Character, VolumePause, PartPause, ChapterPause, ParagraphPause, Pause    |
| `Text`              | string | yes      | spoken / narration text                                                                                    |
| `CharacterId`       | Guid   | yes      | FK → Character — null until attributed; Narration items use Narrator                                       |
| `VoiceInstructions` | string | yes      | max 3000, JSON expression hints                                                                            |
| `AudioFileName`     | string | yes      | relative path to generated WAV                                                                             |

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
| `Alias`       | string | no       | max 250        |

### Voice

| Column                            | Type     | Nullable | Notes                                        |
| --------------------------------- | -------- | -------- | -------------------------------------------- |
| `Id`                              | Guid     | no       | PK                                           |
| `CharacterId`                     | Guid     | no       | FK → Character                               |
| `Name`                            | string   | no       | max 250                                      |
| `Description`                     | string   | yes      |                                              |
| `Source`                          | enum     | no       | `VoiceSource` (Uploaded / Designed / Cloned) |
| `DesignPrompt`                    | string   | yes      | text description used for voice design       |
| `Transcript`                      | string   | yes      | transcript of reference audio (cloning)      |
| `AudioFileName`                   | string   | yes      | relative path to reference WAV               |
| `VoiceDesignSettingsOverrideJson` | string   | yes      | per-voice TTS design settings override       |
| `TtsSettingsOverrideJson`         | string   | yes      | per-voice TTS generation settings override   |
| `CreatedUtc`                      | DateTime | no       |                                              |

### VoiceRule

Ordered per-Character rules that select which Voice to use over a position range. See CONTEXT.md for full evaluation semantics.

| Column        | Type             | Nullable | Notes                                             |
| ------------- | ---------------- | -------- | ------------------------------------------------- |
| `Id`          | Guid             | no       | PK                                                |
| `CharacterId` | Guid             | no       | FK → Character                                    |
| `VoiceId`     | Guid             | no       | FK → Voice                                        |
| `Rank`        | string           | no       | BINARY fractional key — lower = higher priority   |
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
