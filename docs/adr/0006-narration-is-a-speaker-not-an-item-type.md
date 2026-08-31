# Narration is a speaker, not an item type

## Status

accepted

## Context

A ParagraphItem carried the narration/dialog distinction twice. `ParagraphItemType` said
`Narration` or `Character`, and `CharacterId` said who speaks — with narration items already
stamped with the narrator sentinel at import ([ADR 0004](0004-narrator-identity-read-time-projection.md)).
Two columns, one fact, and they could disagree.

They did. `ParagraphSplitter`'s quote scan is the sole authority on boundaries and kinds
([ADR 0005](0005-frozen-paragraph-item-boundaries.md)), and it is regularly wrong: dialog with
unusual punctuation lands as narration, a narrative aside inside quote marks lands as dialog. The
user could see the mistake in the tree and could not reach it — a Narration item has no character
picker, and the character picker has no way to say "this is narration". Two guards enforced the
trap: `SetItemCharacterCommand` silently rejected a character on a narration item, and
`AttributeItemsHandler` skipped narration indices outright. Both were correct *while*
`VoiceResolver` read the type and ignored the speaker: an item showing a character that nothing
would read is worse than an item the user cannot fix. The result was baked audio in the wrong
voice, counted as complete, surviving into the exported `.m4b`, repairable only by re-splitting
the book and throwing away every attribution decision in the paragraph.

Each consumer also had to pick which column it trusted. `VoiceResolver` trusted the type, the
readers trusted the type, the API reported the type, the writers maintained the speaker.

## Decision

The speaker wins. `ParagraphItemType`'s `Narration` and `Character` members collapse into a single
`Speech` member; the five pause members stay, so the type answers exactly one question — is this a
pause? What a speech item *is* becomes a pure function of `CharacterId`:

| `CharacterId` | Means |
| --- | --- |
| `null` | unattributed dialog — the attribution queue's unit of work |
| `ProjectDbContext.NarratorId` | narration |
| any other | attributed dialog |

"Unattributed narration" is not a concept: the splitter deciding a segment is narration *is* its
attribution, so import stamps the narrator rather than setting a type.

`NarrationRule` (`Read2Me.Data`) is the one place that rule lives — `IsNarration(ParagraphItem)`
for a loaded item, `IsNarrationExpression` for readers that must ask inside LINQ that reaches SQL,
the predicate being the compiled expression. Readers, resolvers, command handlers, the API DTO and
the row view models all ask through it instead of comparing to the sentinel, or to a type, inline.

Any speech item may be assigned to any speaker, in either direction, from the character picker the
user already uses to attribute dialog, with the narrator as a pinned entry in it. Boundaries stay
frozen: a flip changes the speaker and nothing else.

### The sentinel rule survives a narrator link

`NarrationRule` compares against the *stored* sentinel. It does not resolve the narrator link, and
must not: under a link, picking "Narrator (Alice)" stamps `NarratorId` and picking "Alice" stamps
Alice's id, and the two stay distinguishable forever — narration keeps resolving through the
narrator's own Voice Rules, Alice's dialog through hers. Who the narrator *is* remains
`NarratorIdentity`'s read-time projection, and ADR 0004's write-time rule — narration stamps the
seed `NarratorId` forever, never the linked character — is unchanged. Unlinking is still one
nulled column with nothing stranded.

### Assigning to the narrator is also the re-attribution lock

Attribution asks about every non-narrator item and skips narrator-stamped ones. That is the old
"skip narration" filter expressed against the speaker, and it gives the user a second gesture for
free: an item assigned to the narrator is an item the queue will not re-ask. Clearing a speaker is
its inverse — "put this back in the queue as unattributed dialog" — so the user can hand an item
back to the LLM instead of hand-picking it. These are the only per-item protections; there is no
general "manually set" bit.

## Considered options

- **Keep the type, add a user-facing retype command (rejected).** Leaves the two columns and their
  capacity to disagree exactly where they were, and every consumer still choosing which to trust.
  It buys the UX fix and none of the modelling fix.
- **Keep the type, derive it from the speaker (rejected).** The disagreement cannot happen, but the
  member survives as a second name for a fact, and the next writer stamps it out of habit.
- **Collapse into the speaker (chosen).** One column, one rule, one seam. Cost: an enum member
  change, a data migration on every project DB, and ~187 call sites moved off
  `ParagraphItemType.Narration` / `.Character`.

## Consequences

- **The migration is the irreversible step.** It backfills `CharacterId = NarratorId` on every
  `Narration` row *before* collapsing the type — which is also what rescues `TitleInserter`'s
  null-speaker title rows from landing in the attribution queue as unattributed dialog — then
  rewrites the old `Character` value to `Speech`. Set-based, idempotent, safe over a database that
  already satisfies the invariant.
- **`VoiceResolver` now honours the speaker on every speech item**, and `AudioItemResolver` follows
  for its speaker label and its "no character assigned" outcome. This is what makes a narration
  item pointing at a character safe, and it is why it had to land together with the guard
  deletions: in between, the user could create items that show a speaker nothing would read.
- **The retired guards' "audio-inert" doc-comments go with them**, replaced by this decision.
  Reading them without this ADR would invite reintroducing the trap.
- **Derived state can flip.** "Character paragraph" — the unit of attribution selection, roll-up
  denominators and node status badges — becomes "paragraph with at least one non-narrator speech
  item", which one assignment can switch on or off. A speaker change therefore clears selection and
  reseeds counts, the same discipline structural changes already follow; counters are never patched
  incrementally.
- **A manual flip clears the item's `AudioFileName`**, returning it to Generatable so the wrong
  voice cannot survive into the export. **Known gap, deliberately left open:** the LLM stamping
  path (`AttributeItemsCommand`) does *not* clear audio, so a queue run that changes a speaker
  leaves stale audio in the old voice behind. That gap predates this decision; closing it would
  mean a queue run could silently invalidate audio across a whole book, which is a larger change
  than this one is willing to make. It is recorded here rather than fixed.
- **Nothing outside changes shape.** `ParagraphItemDto.ItemType` still reports `narration` or
  `dialog` (now derived from the speaker) so agent clients are untouched, and the LLM still receives
  narration items as labelled `narration` context, so attribution quality does not move.
- **Not changed, deliberately**: how the splitter decides narration from dialog at import — only
  where that decision is recorded; frozen item boundaries beyond ADR 0005's retype clause; and
  `NarratorOnlyMode`, which stays a resolve-time override that stamps nothing.
