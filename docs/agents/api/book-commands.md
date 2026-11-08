# Book commands (edit anything)

One endpoint drives every book mutation:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' \
  -d '{ "type": "SetParagraphCharacter", "paragraphId": "<id>", "characterId": "<id>" }'
```

`type` is the command record name without the `Command` suffix (`CreateCharacter`,
`UpdateChapterTitle`, `AddPauses`, `InsertPauseParagraph`, `DeleteVoice`, …); the
error for an unknown type lists every valid one. The project folder always comes
from the URL — a `folderId` in the body is ignored. Split/create commands return
`{ "newEntityId": "<guid>" }`.

`InsertNode` creates one node beside or inside an anchor (**Node insertion**):

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' \
  -d '{ "type": "InsertNode", "anchorKind": "Chapter", "anchorId": "<id>", "placement": "AtStart", "text": "A new paragraph." }'
# → { "newEntityId": "<new paragraph id>" }
```

`anchorKind` is `Volume`, `Part`, `Chapter`, `Paragraph` or `ParagraphItem`; `placement` is
`Before` / `After` (a sibling at the anchor's own level) or `AtStart` / `AtEnd` (a first or last
child one level down). The new node's kind follows from both:

| Anchor | Before / After | AtStart / AtEnd |
| --- | --- | --- |
| `Volume` | Volume | Part |
| `Part` | Part | Chapter |
| `Chapter` | Chapter | Paragraph |
| `Paragraph` | Paragraph | Speech item |
| `Paragraph` that is a pause paragraph | Paragraph | refused |

The book overview (`GET /api/projects/{folder}/book`) carries this table as `structureRules`, one
entry per anchor: `{ anchor, nodeKind, placements: [{ placement, creates, textRequired }], minimumCount }`,
enums by name. `anchor` is `Volume`, `Part`, `Chapter`, `Paragraph`, `PauseParagraph` or
`ParagraphItem` (whose `Before` / `After` are `InsertParagraphItem`'s); `nodeKind` is the `anchorKind`
to send; `minimumCount` is how many of the kind a book keeps (Volume: 1, the others 0). The
planner reads the same table, so an insert it lists is accepted and any other is refused.

A new **Volume, Part or Chapter** is born empty — fill it by inserting at its start or end, using
the returned id as the next anchor. `title` is optional and trimmed; blank leaves it untitled
(rename it later with `UpdateVolumeTitle` and friends). A new **Paragraph** is born holding `text` as
one unattributed speech item — no speaker, no delivery cue, no audio — so it joins the
attribution queue; `text` is required and trimmed. A **speech item** at a paragraph's start or
end is born the same way, first or last inside it — ahead of a leading pause item or after a
trailing one, which `InsertParagraphItem` cannot anchor on; it returns the new item's id. Unlike most
commands it rejects rather than no-ops: blank paragraph or item text, an item at a pause
paragraph's start or end, a combination that makes no node (any `ParagraphItem` anchor
included — use `InsertParagraphItem`), or an unknown anchor is **422**.

`InsertParagraphItem` (`anchorItemId`, `position` `Before` / `After`, `text`) follows the same
**Item insertion** rules as `InsertNode` at a paragraph's start or end: blank `text`, or an anchor
that is a pause item, is **422** — one invalid request, one refusal, whichever route asked.

```bash
# An empty part at the end of a volume, then its first chapter
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' \
  -d '{ "type": "InsertNode", "anchorKind": "Volume", "anchorId": "<id>", "placement": "AtEnd", "title": "Part Two" }'
# → { "newEntityId": "<new part id>" }
```

A book always keeps at least one volume: `DeleteVolume` on its only volume is **422**
("A book must keep at least one volume.") and deletes nothing. An unknown volume still
no-ops.

`SetNarratorCharacter` is the one project-scoped command — it names the Character who
narrates the book (Sherlock Holmes narrated by Dr. Watson), so narration and that
character's dialog share one voice:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' \
  -d '{ "type": "SetNarratorCharacter", "characterId": "<id>" }'   # null unlinks
```

Unlike its siblings it rejects rather than no-ops: a `characterId` naming no character
in this project, or the seed Narrator row itself (a self-link *is* the unlinked state),
comes back **422** with the reason.

`SetItemDeliveryCue` adds, edits or removes one speech item's **delivery cue** (the
volume-and-manner hint TTS reads, stored as `voiceInstructions`) — narration and
dialog alike:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' \
  -d '{ "type": "SetItemDeliveryCue", "itemId": "<id>", "cue": "whispered, urgent" }'   # null or "" removes
```

The cue is trimmed; blank means none. A real change discards the item's audio and any
audio review, as a text edit does, so it is Generatable again; re-sending the stored
cue writes nothing and keeps the audio. Like `SetNarratorCharacter` it rejects rather
than no-ops: an unknown item, a Pause item, or a cue over 200 characters is **422**.
Attribution still overwrites a hand-written cue (and keeps the audio when it does).

`AddTitles` runs several title levels in one request — the web's Add titles dialog sends this:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' -d '{ "type": "AddTitles", "levels": ["Book", "Chapter"] }'
```

`levels` is any of `Book`, `Volume`, `Part`, `Chapter`; each runs as its single-level command
(`AddBookTitle`, `AddVolumeTitles`, …) would, book first and each once whatever the order sent.
Levels with nothing to add are a no-op. No levels is **422**; so is a level's refusal, which stops
the levels after it and names the ones that had already landed (those stay).

## Edit with AI

Say what to change in plain language and let the LLM write the edits. Four steps: plan (what the
instruction means and what it matches), propose (a new value per matched item), review (yours to
correct), apply (one `ApplyBookEdits` command). The plan stays on the host under the opaque
`program` id — you hold ids and values, never the plan. A session expires two hours after its last
use; `DELETE` it when you are done. Titles and paragraph text only.

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/book-edits/plan \
  -H 'content-type: application/json' \
  -d '{ "instruction": "rename every chapter to Chapter {n}", "thinking": false }'
# status Ok carries the program; NoLlmConfigured | Unsupported | ServiceUnavailable | Failed |
# NoTargets explain themselves in reason instead. transform Llm packs items into requests of up to 8 items and ~12,000 characters of text plus
# sources (an item over that goes alone; one over ~60,000 fails alone, "Too long to send to the AI (text and sources)");
# requestCount says how many, from the same packing the run uses, counting only rows that can be sent. Per-row retries too; RegexReplace / SetTemplate / ChangeCase run in code.
# RegexReplace, and ChangeCase with a pattern, count only the items their pattern matches (none → NoTargets);
# a paragraph-text ChangeCase warns "N titles also match — run again targeting <levels> titles" when titles match too.
# 409 "Project is restoring" while a Restore runs, and also when one began after the plan was asked for:
# the plan's targets would predate it, so plan again once it has finished. A web client also drops an answered
# program whose bookEdit dropped message reached the hub first.

# start the run (202) and follow it on the hub as bookEdit Progress, then Done or Failed —
# pass the connectionId you want it pushed to, or leave it out and poll the session instead:
curl -s -X POST http://localhost:5000/api/projects/{folder}/book-edits/{program}/propose \
  -H 'content-type: application/json' -d '{ "thinking": false, "connectionId": null }'
curl -s http://localhost:5000/api/projects/{folder}/book-edits/{program}
# row status: Proposed (usable) | NoChange (the value equals the current text) | Failed
# SetTemplate copies Edit sources through tokens beside {n} and {old} ("Chapter {p1}, {p2}"; names in
# EditSourceCatalog), trimmed; a row whose source is missing or its own paragraph is Failed with the reason
# ("p2 is missing: the chapter has only 1 paragraph", "chapter is missing: the chapter title is blank",
# "p1 is the paragraph being edited"); a source the target can't read, or past 5, makes the plan Unsupported.
# An Llm plan's transform.sources travel with each batch entry as "sources": {name: value} (only the names the plan
# names); a row with a missing or self source is Failed with the same reasons before any request, keeps its place in the
# rows, and never occupies an index in a request. propose-one re-sends the row's stored sources (the hint adds none),
# and a missing-source or too-long row answers Failed without a request.
# Every row (the run read, the hub's Done rows and the propose-one answer share one shape) carries "sources": the values
# the plan read for it, as [{ "name": "p1", "label": "Paragraph 1", "value": "1" }, ...] in the order the plan names
# them (empty when the plan reads none; a missing source is left out, the failureReason names it), and
# "canAskAgain": false on a missing- or self-source Failed row, where propose-one would only fail the same way.
# The plan summary ends " — reads p1 (paragraph 1), p2 (paragraph 2)" for a plan that reads sources.

# stop early — the rows already computed stay reviewable and appliable:
curl -s -X POST http://localhost:5000/api/projects/{folder}/book-edits/{program}/cancel   # 204

# re-ask for one row, steered by a hint (Llm plans only):
curl -s -X POST http://localhost:5000/api/projects/{folder}/book-edits/{program}/propose-one \
  -H 'content-type: application/json' \
  -d '{ "targetId": "<row id>", "hint": "keep the original spelling of names", "thinking": true }'

# apply the rows you kept — one command, one commit, values yours to edit first:
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' \
  -d '{ "type": "ApplyBookEdits", "edits": [ { "kind": "ChapterTitle", "id": "<row id>", "newValue": "Chapter 1" } ] }'
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/book-edits/{program}   # 204
```
