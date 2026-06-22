# Voice Rule position bounds anchor on node ids, not Order-path snapshots

## Status

accepted

## Context

A Voice Rule applies over a Position range in the story. Each bound (`From` / `To`) must name a place in the hierarchy. The story is linearly ordered by sibling-scoped fractional `Order` keys per level; an item's absolute Position is the tuple `(Volume.Order, Part.Order, Chapter.Order, Paragraph.Order, ParagraphItem.Order)`. We had to choose what a bound *stores*.

## Decision

A bound stores a `(VoiceAnchorLevel, NodeId)` pair — a reference to an actual hierarchy node — resolved at evaluation time to a Position span (subtree min for `From`, subtree max for `To`). The alternative was snapshotting the node's `Order`-path strings into the rule.

## Considered options

- **Order-path snapshot.** Survives id churn but a structural edit shifts paths, silently drifting a rule's bounds to cover the wrong content. Rejected: silent wrong-output is worse than a visibly broken rule.
- **Node-id reference (chosen).** Matches how the rest of the app anchors (FK ids) and resolves to live node names for display. Cost: ids regenerate on any structural change — split / merge / **reread** (reread preserves Characters and Voices but clears all Volume…ParagraphItem rows), so an anchor can dangle.

## Consequences

- A rule whose anchor node no longer exists is **dangling**: skipped at evaluation (treated as not-passing) and flagged in the UI for re-anchoring — never silently deleted or repointed. Cascade-delete applies only when the rule's *target Voice* is deleted.
- The default rule has null anchors, so it never dangles and always passes — guaranteeing evaluation always yields a voice.
