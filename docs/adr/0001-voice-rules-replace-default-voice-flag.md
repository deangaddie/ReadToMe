# Voice selection is rule-based; the per-Voice default flag is removed

## Status

accepted

## Context

A Character has many Voices. Picking which Voice synthesizes a given audio item used to be a single fact — the Voice with `IsDefault = true` (chosen in `AudioQueueProcessor`). The product now needs a Character's voice to vary by **position in the story**: a different voice from a point onward (a character grows up), or a specific voice for a given Volume/Part/Chapter/Paragraph/ParagraphItem.

## Decision

We introduce **Voice Rules** — an ordered, per-Character list of rules, each targeting a Voice over a Position range, evaluated top-to-bottom with **the last passing rule winning**. Every Character with at least one Voice has exactly one pinned, always-passing **default rule** (null anchors) at the top. We **delete `Voice.IsDefault`**: "the default voice" is now expressed solely as the default rule's `VoiceId`. The logic that lived on the flag (first voice becomes default; reassign on delete) moves into default-rule management.

## Considered options

- **Keep `Voice.IsDefault`, add rules alongside it.** Rejected: two mechanisms for "which voice" that must stay in sync — the classic dual-source-of-truth bug.
- **Default as an implicit eval fallback (no stored rule).** Rejected: makes evaluation non-uniform (a special fallback branch) and the default invisible/unmanageable in the rules UI.

## Consequences

- One-way migration: existing `IsDefault` voices become each Character's default rule; the column is dropped. Reversing means reintroducing the flag and back-deriving it.
- `AudioQueueProcessor`'s voice-pick block is replaced by a call to a pure `VoiceRuleEvaluator`.
- A character with no voices has no rules and (as before) cannot generate audio.
