# The narrator link resolves at read time through `NarratorIdentity`

## Status

accepted

## Context

A book may declare that the Narrator *is* one of its Characters — Sherlock Holmes narrated by Dr. Watson. Narration and that character's dialog then share one voice. Narration items already stamp `CharacterId = ProjectDbContext.NarratorId` at write time, from three separate writers (`ApplySegmentationHandler` — retired by [ADR 0005](0005-frozen-paragraph-item-boundaries.md) — `NarrationClassifier` via `BookContentPersister`, `TitleInserter`). We had to choose what setting the link *does* to those rows.

## Decision

The link is a plain `Projects.NarratorCharacterId` (`Guid?`), and it is resolved at **read time** by `NarratorIdentity` — a `readonly record struct (CharacterId, DisplayName, IsLinked)` in `Read2Me.Data`. Narration keeps stamping the seed `NarratorId` forever. The alternative was restamping narration items with the linked character's id when the link is set.

## Considered options

- **Restamp on link.** Every downstream reader keeps working untouched. Rejected: three write sites would have to stay in step forever, the rewrite is a whole-book UPDATE, and it is **irreversible** — once narration carries Watson's id, nothing distinguishes it from Watson's dialog, so unlink cannot be undone.
- **Read-time projection (chosen).** One substitution site (`VoiceResolver`) against three write sites, and unlink is a bare column-null with nothing stranded. Narrator Voice Rules and voices go dormant and wake unchanged. Cost: every consumer that wants "who narrates" must go through the seam.

## Consequences

- **`NarratorIdentity.LoadAsync` is the only reader of `Project.NarratorCharacterId`**, outside the command handlers that write it (`SetNarratorCharacter`, and the delete/merge lifecycle fix-ups). Projection's known failure mode is a consumer that forgets the seam and silently renders "Narrator"; this access rule is the mitigation, and it is enforced by a source-scanning test (`NarratorCharacterIdAccessRuleTests`) with a named allowlist.
- **No EF relationship and no FK** on the column — matching `Project`'s flat style, and deliberately: a link pointing at a deleted Character resolves to `Unlinked` rather than throwing. A dangling pointer self-heals instead of failing audio for a whole book.
- `DisplayName` is the linked character's primary `Name`, never an alias — the attribution model echoes it back and it must match a roster name.
- No DI interface and no new service: consumers call `LoadAsync` on the `ProjectDbContext` they already hold, so the link rides existing queries rather than adding round-trips to the audio hot path.
