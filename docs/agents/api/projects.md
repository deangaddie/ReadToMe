# Create a project and import the book

```bash
curl -s -X POST http://localhost:5000/api/projects \
  -F title="My Book" -F bookTitle="My Book" -F author="A. Author" \
  -F file=@/path/to/book.epub                        # → { "folderName": "..." }

curl -s -X POST http://localhost:5000/api/projects/{folder}/import \
  -H 'content-type: application/json' -d '{ "reread": false }'
```

Both imports answer 204. `reread: true` clears existing content first (safe way to re-import).

A manual reread re-splits the stored file by hand-chosen rules instead of the automatic
reader (the book page's "Manual reread" dialog on the wire). It also replaces existing content:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/import/manual \
  -H 'content-type: application/json' \
  -d '{ "hasMultipleVolumes": false, "hasMultipleParts": true,
        "part": { "mode": "Prefix", "prefix": "Part" },
        "chapter": { "mode": "Roman" } }'
```

`mode` is `Prefix` (needs a non-blank `prefix`), `Arabic` (bare numbers) or `Roman`. `volume` is
read only when `hasMultipleVolumes`, `part` only when `hasMultipleParts`; `chapter` is always
required. A switched-on level without a valid rule is 400; a reader failure is 422.

The book writes (`/commands`, both imports, pronunciation add/edit/delete, voice audio upload and
transcribe) accept an optional `X-Origin-Id` header (a GUID); the project PATCH, cover and
narrator-only writes ignore it.
It is echoed as `originId` on the mutation receipt the live hub publishes, so a client can
recognise its own commits among everyone else's. Absent or malformed, the receipt is unattributed.

Inspect the result: `GET /api/projects/{folder}/book` (overview; its `revision` is the book
revision the read ran against, stamped before it as `/status` stamps its own;
its `structureRules` are the **Structure rules** described under `InsertNode` in [book-commands.md](book-commands.md)), then walk
`GET /api/projects/{folder}/nodes/{level}/{id}/children` (volume → part → chapter;
chapter children carry the paragraphs with their items). A paragraph's `isPauseParagraph`
means no items, or a single pause item. An item's `itemType` is `Narration` or `Character` for
speech, derived from its speaker (ADR 0006), or a pause kind. `voiceInstructions` is the item's
delivery cue; items arrive in `orderKey` (fractional position key) order.

`GET /api/projects/{folder}` returns the project metadata, including
`narrator: { characterId, displayName, isLinked }` — who narrates the book.
`isLinked: false` is the normal case: nobody in the cast narrates, `characterId` is
the seed Narrator row and `displayName` is `"Narrator"`. There is no null case. Set
or clear the link with `SetNarratorCharacter` ([book-commands.md](book-commands.md)).

Project-level metadata and the cover image:

```bash
curl -s -X PATCH http://localhost:5000/api/projects/{folder} \
  -H 'content-type: application/json' -d '{ "title": "New title", "author": "A. Author" }'
  # omitted fields stay as they are; a blank title/bookTitle is 400; the folder name never changes

curl -s -X PUT http://localhost:5000/api/projects/{folder}/narrator-only-mode \
  -H 'content-type: application/json' -d '{ "enabled": true }'     # → 204

curl -s -X PUT http://localhost:5000/api/projects/{folder}/cover \
  -F file=@/path/to/cover.jpg                        # jpg/jpeg/png/webp ≤ 10 MB → { "coverImage": "cover.jpg" }
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/cover   # → 204, also when there was none

curl -s -X DELETE http://localhost:5000/api/projects/{folder}          # → 204; the folder and everything in it
```

The cover is served at `/workspace/{folder}/{coverImage}`; `GET /api/projects` lists
`coverImage` and `fileType` (`Epub` | `Text`) per project alongside the audio counters.
`GET /api/projects` and `GET /api/projects/{folder}` also carry `series: { seriesId, name, position } | null`.

## Project status and checkpoints

Where the project stands, in one read:

```bash
curl -s http://localhost:5000/api/projects/{folder}/status
curl -s http://localhost:5000/api/projects/{folder}/nodes/Chapter/{chapterId}/status   # one node's summary
```

Units differ: `items` counts items, while `attribution`, `audio`, `review` and `pauseParagraphs`
(no items, or one pause item) count paragraphs; `nodes` holds the same roll-ups per volume, part
and chapter id. `characters` excludes the seed Narrator row; `charactersWithLines` counts speakers (narration
included, credited to the linked narrator when there is one) and `readyVoices` how many of them
have a voice with audio. Both reads reseed the node roll-ups from the database, so they are
current even with no UI open, and hub clients receive the corrected `nodeStatus` deltas.

A Pipeline step the user has checked holds a **checkpoint**. `checkpoints` lists only the steps
that have one, keyed by step id (`Import | Structure | Speakers | Text | Voices | Titles |
Pauses | Audio | Export`), the same name as the route segment; a checkpoint's `state` is
`Confirmed` or `Skipped`. Set, replace or clear one:

```bash
curl -s -X PUT http://localhost:5000/api/projects/{folder}/checkpoints/Structure \
  -H 'Content-Type: application/json' -d '{"state":"Confirmed"}'   # Confirmed | Skipped | Open → 204
```

Structure, Text and Titles take `Confirmed` or `Skipped`, Speakers only `Confirmed` and Pauses
only `Skipped`. The other steps are done by their data, so any state on them is a 422, `Open`
included. `Open` deletes the row. Repeating the state a row already holds changes nothing and
still answers 204. The same state on a stale row reconfirms it. The refreshed `/status` arrives
through the `Checkpoints` receipt. A step that isn't one of the nine ids is a 404. A book with no
content is a 422 "Read the book first", and a body without `state` is a 400. A reread or a clear
deletes every checkpoint.

A Book change that disturbs a checkpoint marks it `stale` in the same commit, keeping its `state`
and `at`, and that command's receipt also carries `Checkpoints`. A split at a Part or Chapter, or a
Volume, Part or Chapter merge, delete or insert, stales Structure, Titles and Pauses. A Volume, Part or Chapter rename
stales Titles. `ApplyBookEdits` stales Text when it rewrote item text and Titles when it rewrote a
title. Nothing else stales a checkpoint, including paragraph and item edits, Add titles and Add
pauses.
