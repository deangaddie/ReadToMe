# Paragraph item boundaries are frozen: attribution assigns speakers, never splits

## Status

accepted

## Context

Attribution used to ask the LLM for a paragraph's **whole segment list** — text, type and speaker —
and reconcile that answer against the stored ParagraphItems, so the model could re-split a paragraph
whose import-time split was wrong. That gave the model a repair for a bad quote scan, and with it the
power to create and delete items, and to restate book text.

## Decision

The LLM is now asked only *who speaks item N*, answering `[{index, speaker, voice_instructions}]`
against the items that already exist. `ParagraphSplitter` (the deterministic quote scan run at
import) is the sole authority on item boundaries for the life of the project.

## Considered options

- **Keep the segment ask (rejected).** The trade that decided it: a model re-split is unfixable by
  the user. There is no UI to split one item into two, so a wrong boundary is permanent and
  invisible, while the *thing it was fixing* — a bad quote scan — was at least predictable.
- **Freeze boundaries (chosen).** The split becomes deterministic and inspectable, and the repair
  moves into the user's hands (see [`.scratch/manual-item-editing/`](../../.scratch/manual-item-editing/map.md),
  which owns the missing split-item command). Cost: an item the scan got wrong stays wrong until
  that command exists.

## Consequences

- **A defect class is retired by construction, not patched.** The narration-swallow defect
  (`.scratch/narration-swallow/`) — an all-narration answer deleting a dialog item, marking the
  paragraph complete and sealing it out of the re-queue filter — cannot occur when no answer can
  delete an item. `EscalationTrigger.DialogLost` and its guard are deleted with it.
- **Book text can no longer be corrupted by the model**, because item text never round-trips through
  it. `SegmentAligner` — 130 lines of punctuation-tolerant snapping that existed only to defend
  against that — is deleted.
- **New permanent failure mode**: an item that genuinely holds two speakers (bad quote scan, or two
  speakers inside one quote pair) is unanswerable. The prompts route it to `unknown`, which surfaces
  it to the user as unattributed rather than silently mis-stamping. Its rate is measured as part of
  the change.
- **Not changed, deliberately**: `ParagraphSplitter` itself (its em-dash and unquoted-dialog gaps
  stand), the narrator wire-alias rules of [ADR 0004](0004-narrator-identity-read-time-projection.md)
  (linked → canonicalize; unlinked `narrator`-on-dialog → `unknown`), and existing projects, whose
  already-re-split items are left exactly as they are — no migration re-splits stored books.
- **`ParagraphItemType` survives in the database and app** even though the LLM no longer answers a
  type. A user-driven narration↔dialog switch is expected later; the model does not get one.
- **Amended 2026-08-31**: the retype clause above is retired — the user-driven narration↔dialog switch it anticipated arrived as a speaker assignment, not a type change, and `ParagraphItemType` no longer distinguishes the two (see [ADR 0006](0006-narration-is-a-speaker-not-an-item-type.md)); everything else here still binds, in particular that attribution may never insert, delete, reorder or retext an item.
- **Amended 2026-09-01**: the deferred cost above — "an item the scan got wrong stays wrong until that command exists" — is now partly discharged. The manual item editing work ([`.scratch/manual-item-editing/`](../../.scratch/manual-item-editing/map.md)) shipped insert-before/insert-after for paragraph items, so an item that genuinely holds two speakers is repairable in the app: edit the merged item's text down to one speaker, insert a sibling for the remainder, and assign each its character. The one-action split-item command that ticket set out is still open; this is the same repair in three gestures. Nothing else here is relaxed — attribution still may never insert, delete, reorder or retext an item. Insertion is a *user* gesture and never an LLM one: the freeze binds the model, not the producer of the item list.
