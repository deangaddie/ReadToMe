# Discover characters, attribute dialog

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/discover
# thin or wrong cast? re-run with the model's thinking phase on (slower, better recall):
curl -s -X POST 'http://localhost:5000/api/projects/{folder}/characters/discover?thinking=true'
# review the rows, then persist the ones you keep:
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/discover/apply \
  -H 'content-type: application/json' \
  -d '[ { "name": "Alice", "aliases": ["Al"] } ]'
# each discovered row carries existingCharacterId when it resolves onto a roster character (by name
# or alias); collisions lists names two characters would share once every row is applied.

# queue attribution per chapter (or part/volume):
curl -s -X POST http://localhost:5000/api/projects/{folder}/attribution/enqueue \
  -H 'content-type: application/json' \
  -d '{ "level": "Chapter", "nodeId": "<chapterId>", "unprocessedOnly": true }'
# or an explicit selection (ids without dialog are ignored; enqueued = what was queued):
curl -s -X POST http://localhost:5000/api/projects/{folder}/attribution/enqueue-paragraphs \
  -H 'content-type: application/json' \
  -d '{ "paragraphIds": ["<paragraphId>", "<paragraphId>"] }'
# the Character paragraphs under a node with their chapter/part/volume ids
# (what a selection holds); unprocessedOnly=true keeps those still unattributed:
curl -s 'http://localhost:5000/api/projects/{folder}/nodes/Chapter/{chapterId}/paragraph-ids?unprocessedOnly=true'
# poll /api/attribution/queue; per-paragraph queue state (status + failure/unknown outcome):
curl -s http://localhost:5000/api/projects/{folder}/attribution/paragraphs/{paragraphId}
# forget a paragraph's Failed/Unfinished outcome (204):
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/attribution/paragraphs/{paragraphId}/outcome
# cancel everything queued (204); dismiss retires the finished run's throughput summary in the UI (204, idempotent):
curl -s -X POST http://localhost:5000/api/attribution/cancel
curl -s -X POST http://localhost:5000/api/attribution/dismiss
# the attribution itself is per item — read it off the paragraph's items:
curl -s http://localhost:5000/api/projects/{folder}/nodes/Chapter/{chapterId}/children
# the cast page's reads: every character as a roster row (narrator first; lineCount, voiceCount,
# readyVoiceCount, isNarrator = the seed row, narratesBook = the linked narrator), a character's
# lines in book order, and a line's surrounding paragraphs (before/after each 0..10, default 3/2;
# 404 when the paragraph is not in the chapter) with the speaker name per item:
curl -s http://localhost:5000/api/projects/{folder}/characters/summary
curl -s http://localhost:5000/api/projects/{folder}/characters            # the plain roster: id, name, aliases
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/lines
curl -s 'http://localhost:5000/api/projects/{folder}/paragraphs/{paragraphId}/context?chapterId={chapterId}&before=3&after=2'
```

Manual fixes go through the generic commands endpoint ([book-commands.md](book-commands.md)), e.g.
`SetParagraphCharacter`, or `SetParagraphsCharacter` for a whole selection — preview what it
would write first:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/bulk-assign-preview \
  -H 'content-type: application/json' -d '{ "paragraphIds": ["<paragraphId>"] }'
```

A book that marks speech without quote marks (a transcript's `Q. …` / `A. …` paragraphs) imports
as narration, which attribution skips and bulk assign never writes. `ReleaseNarration` clears the
narrator stamp, and the audio, from every item of a scope's released paragraphs, leaving them
unattributed dialog. `scope` is `Part`, `Chapter` or `Paragraph` — there is no whole-book scope.

- **`Part` / `Chapter`** release only the **narration-only paragraphs** (no dialog item at all) that
  carry a **speech marker** repeated in at least 5 narration-only paragraphs of their own chapter:
  an initial prefix (`Q.`, `A:`), a name prefix naming a Character or alias of the book
  (`Darcy:`), or em-dash speech (`—What is it?— she asked.`). The real narration around a
  transcript keeps the narrator. Each chapter of a Part is judged by its own markers. The
  thresholds are fixed; there is no unfiltered Chapter/Part release (ADR 0015).
- **`Paragraph`** is unfiltered: it releases that narration-only paragraph whatever its text says —
  the escape hatch for a line the filter misses.

Speech tags inside quoted paragraphs stay narration at every scope. Preview it first with the same
`{ scope, nodeId }`:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/narration/release-preview \
  -H 'content-type: application/json' -d '{ "scope": "Chapter", "nodeId": "<chapterId>" }'
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' -d '{ "type": "ReleaseNarration", "scope": "Chapter", "nodeId": "<chapterId>" }'
```

`paragraphs` is what the release would write and `itemsWithAudio` the audio it would discard.
`speechMarkers` names the markers that passed (e.g. `A.` and `Q.`, 36 paragraphs each), keyed as written (`Q.` and `Q:` are separate;
em-dash speech is `—…—`), each with the released paragraphs it covers summed across a Part's
chapters, ordered by count descending, then marker. It is empty for a `Paragraph` scope and when
nothing passes.

Released paragraphs are Character paragraphs from then on: `paragraph-ids` lists them,
`SetParagraphsCharacter` stamps them, and `attribution/enqueue` (which the release never calls)
sends them to the LLM. Nothing to release — including a Chapter or Part where nothing passes the
filter — is a no-op with no revision, so a second release is harmless. `SetParagraphsCharacter`
with the narrator's id undoes it.

A speaker the roster does not have yet is created and assigned in one request with
`CreateCharacterAndAssign`: `name` (trimmed) plus exactly one of `itemId` (as `SetItemCharacter`),
`paragraphId` (as `SetParagraphCharacter`) or `paragraphIds` (as `SetParagraphsCharacter`). Like
`CreateCharacter` it is idempotent by name — a name or alias the roster already answers to is
assigned, not doubled — and it answers that character's id:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' \
  -d '{ "type": "CreateCharacterAndAssign", "name": "Jane Bennet", "paragraphIds": ["<id>", "<id>"] }'
```

Unlike the single-step speaker commands it rejects rather than no-ops: a blank name, no target or
more than one, or an empty `paragraphIds` is **422** with nothing written; an unknown target is
**422** too, though the character has been created by then.
