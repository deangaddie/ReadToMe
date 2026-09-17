# Agent API — workflow guide

HTTP API for driving the full audiobook production cycle without the UI. Minimal
APIs inside the app process: start the app (`dotnet run --project src/Read2Me.App`)
and talk to `http://localhost:5000/api/...`. Localhost only, no auth.

**Shapes**: this guide names endpoints and order only. Request/response schemas live
in the OpenAPI document — fetch `GET /openapi/v1.json` first and treat it as the
source of truth.

**Errors**: RFC 7807 ProblemDetails. 400 malformed input, 404 unknown folder/entity,
409 already-running or blocked (assembly carries `audioRemainingCount`), 422 domain
failure (message in `detail`).

**Polling pattern**: the four long operations (attribution, audio, voice batch,
assembly) return `202 Accepted` and run in the background. Poll their status
endpoint until idle, then read per-item outcomes:

```bash
# generic wait loop (attribution shown; same shape for /api/audio/queue)
while :; do
  s=$(curl -s http://localhost:5000/api/attribution/queue)
  q=$(echo "$s" | jq '.queuedCount + .processingCount')
  [ "$q" = "0" ] && break
  sleep 5
done
```

## 0. Configure services (once)

Config areas: `llm`, `paragraph-tts`, `voice-design`, `transcription`,
`semantic-similarity` — same CRUD per area, `PUT /active` selects. The first config
created in an area auto-activates.

```bash
curl -s http://localhost:5000/api/settings/llm                       # list
curl -s -X POST http://localhost:5000/api/settings/llm \
  -H 'content-type: application/json' \
  -d '{ "name": "local llama", "baseUrl": "http://localhost:8080" }'
curl -s -X PUT http://localhost:5000/api/settings/llm/active \
  -H 'content-type: application/json' -d '{ "id": 1 }'
```

Prompt templates: `GET /api/settings/prompts` (all kinds, resolved),
`PUT /api/settings/prompts/{kind}` to override, `DELETE` to reset.
Audio post-processing scalars: `GET/PUT /api/settings/audio-processing`.
Themes (shared with both UIs): `GET/POST /api/settings/themes`, `PUT/DELETE /api/settings/themes/{id}`
(built-in rows are read-only → 400), `GET/PUT /api/settings/themes/selection`
(`{ selectedThemeId, followSystemPreference }`, both optional on PUT).

Container health (read-only): `GET /api/ai-services`,
`GET /api/ai-services/{name}/status`. Remember the GPU fits one model at a time —
start only the containers the current step needs (`docker compose` in `Infra/`).

## 1. Create a project and import the book

```bash
curl -s -X POST http://localhost:5000/api/projects \
  -F title="My Book" -F bookTitle="My Book" -F author="A. Author" \
  -F file=@/path/to/book.epub                        # → { "folderName": "..." }

curl -s -X POST http://localhost:5000/api/projects/{folder}/import \
  -H 'content-type: application/json' -d '{ "reread": false }'
```

`reread: true` clears existing content first (safe way to re-import).

A manual reread re-splits the stored file by hand-chosen rules instead of the automatic
reader (the Blazor "Manual Reread" dialog on the wire). It also replaces existing content:

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

Every write (`/commands` and both imports) accepts an optional `X-Origin-Id` header (a GUID).
It is echoed as `originId` on the mutation receipt the live hub publishes, so a client can
recognise its own commits among everyone else's. Absent or malformed, the receipt is unattributed.

Inspect the result: `GET /api/projects/{folder}/book` (overview), then walk
`GET /api/projects/{folder}/nodes/{level}/{id}/children` (volume → part → chapter;
chapter children carry the paragraphs with their items). Each paragraph has
`isPauseParagraph` (no items, or a single pause item); each item has `id`, `itemType`
(`Narration` | `Character` | a pause kind), `text`, `characterId`, `audioFileName`,
`voiceInstructions`, `orderKey` (fractional position key, items arrive in that order) and
`isPause`.

`GET /api/projects/{folder}` returns the project metadata, including
`narrator: { characterId, displayName, isLinked }` — who narrates the book.
`isLinked: false` is the normal case: nobody in the cast narrates, `characterId` is
the seed Narrator row and `displayName` is `"Narrator"`. There is no null case. Set
or clear the link with `SetNarratorCharacter` (section 5).

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
```

The cover is served at `/workspace/{folder}/{coverImage}`; `GET /api/projects` lists
`coverImage` and `fileType` (`Epub` | `Text`) per project alongside the audio counters.

Where the project stands, in one read:

```bash
curl -s http://localhost:5000/api/projects/{folder}/status
# { hasContent, characters, charactersWithLines, readyVoices,
#   items: { total, withAudio, unattributed },            ← items
#   attribution: { remaining, processing, queued },       ← paragraphs
#   audio: { remaining }, review,                         ← paragraphs
#   volumeIds, nodes: { <volume|part|chapter id>: { attributionRemaining, audioRemaining,
#   review, attributionProcessing, attributionQueued, isDone } }, revision }
curl -s http://localhost:5000/api/projects/{folder}/nodes/chapter/{chapterId}/status   # one node's summary
```

`characters` excludes the seed Narrator row; `charactersWithLines` counts speakers (narration
included, credited to the linked narrator when there is one) and `readyVoices` how many of them
have a voice with audio. Both reads reseed the node roll-ups from the database, so they are
current even with no UI open, and hub clients receive the corrected `nodeStatus` deltas.

## 2. Discover characters, attribute dialog

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
  -d '{ "level": "chapter", "nodeId": "<chapterId>", "unprocessedOnly": true }'
# or an explicit selection (ids without dialog are ignored; enqueued = what was queued):
curl -s -X POST http://localhost:5000/api/projects/{folder}/attribution/enqueue-paragraphs \
  -H 'content-type: application/json' \
  -d '{ "paragraphIds": ["<paragraphId>", "<paragraphId>"] }'
# the Character paragraphs under a node with their chapter/part/volume ids
# (what a selection holds); unprocessedOnly=true keeps those still unattributed:
curl -s 'http://localhost:5000/api/projects/{folder}/nodes/chapter/{chapterId}/paragraph-ids?unprocessedOnly=true'
# poll /api/attribution/queue; per-paragraph queue state (status + failure/unknown outcome):
curl -s http://localhost:5000/api/projects/{folder}/attribution/paragraphs/{paragraphId}
# forget a paragraph's Failed/Unfinished outcome (204):
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/attribution/paragraphs/{paragraphId}/outcome
# cancel everything queued (200); dismiss retires the finished run's throughput summary in the UI (200, idempotent):
curl -s -X POST http://localhost:5000/api/attribution/cancel
curl -s -X POST http://localhost:5000/api/attribution/dismiss
# the attribution itself is per item — read it off the paragraph's items:
curl -s http://localhost:5000/api/projects/{folder}/nodes/chapter/{chapterId}/children
# the cast page's reads: every character as a roster row (narrator first; lineCount, voiceCount,
# readyVoiceCount, isNarrator = the seed row, narratesBook = the linked narrator), a character's
# lines in book order, and a line's surrounding paragraphs (before/after each 0..10, default 3/2;
# 404 when the paragraph is not in the chapter) with the speaker name per item:
curl -s http://localhost:5000/api/projects/{folder}/characters/summary
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/lines
curl -s 'http://localhost:5000/api/projects/{folder}/paragraphs/{paragraphId}/context?chapterId={chapterId}&before=3&after=2'
```

Manual fixes go through the generic commands endpoint (section 5), e.g.
`SetParagraphCharacter`, or `SetParagraphsCharacter` for a whole selection — preview what it
would write first:

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/bulk-assign-preview \
  -H 'content-type: application/json' -d '{ "paragraphIds": ["<paragraphId>"] }'
# → { "paragraphsWithCharacterItems": 1, "characterItems": 2 }
```

## 3. Voices

```bash
# plan voices + design prompts for every character (one LLM call each):
curl -s -X POST http://localhost:5000/api/projects/{folder}/voice-batch/prompts \
  -H 'content-type: application/json' -d '{ "regenerateAll": false }'
# poll /api/voice-batch/status until isRunning=false

# synthesise reference audio for every planned voice:
curl -s -X POST http://localhost:5000/api/projects/{folder}/voice-batch/audio
# poll /api/voice-batch/status

# inspect / regenerate one voice:
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices
curl -s http://localhost:5000/api/projects/{folder}/voices/{voiceId}
# → { id, characterId, name, source: "Uploaded"|"Generated", designPrompt, transcript, audioFileName,
#     isEdited, voiceDesignSettingsOverrideJson, ttsSettingsOverrideJson }
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio

# reference audio: upload/replace (multipart 'file', 200 MB max; normalised + committed), then transcribe:
curl -s -X PUT http://localhost:5000/api/projects/{folder}/voices/{voiceId}/audio -F 'file=@sample.wav'
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/transcribe
# → { "transcript": "..." } (also stored on the voice)

# design prompt without persisting: render the template, edit, send to the LLM, then SetVoiceDesignPrompt:
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/design-prompt/render
# → { "prompt": "<rendered template>" }
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/design-prompt/generate \
  -H 'content-type: application/json' -d '{ "prompt": "<rendered or edited>" }'
# → { "designPrompt": "..." }

# per-voice settings overrides are sparse patches keyed by the provider's settingsJson names;
# the schema per provider type (ranges, defaults) drives an editor:
curl -s 'http://localhost:5000/api/settings/paragraph-tts/schema?type=VoxCpm2'   # VoxCpm2|Chatterbox|ChatterboxTurbo|Qwen3Base
curl -s 'http://localhost:5000/api/settings/voice-design/schema?type=Qwen3'      # VoxCpm2|Qwen3
# → { type, fields: [{ key, label, kind: number|boolean|enum|string|text, min?, max?, step?, options?, default, help?, nullable }] }
# then: SetVoiceTtsSettingsOverride / SetVoiceSettingsOverride commands with json = '{"cfg_value":3.5}' (null clears)

# voice rules: which voice a character speaks in where. The first voice brings the default rule;
# CreateVoiceRule adds an anchored one (fromLevel Volume|Part|Chapter|Paragraph|ParagraphItem +
# fromNodeId; leave to* out for "from here on", repeat the anchor for "just this node"). Rules are
# evaluated in list order and the last passing rule wins; MoveVoiceRule Up|Down reorders the
# non-default ones, DeleteVoiceRule removes one (never the default).
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/voice-rules
# → [{ ruleId, voiceId, voiceName, isDefault, fromLevel, fromNodeId, fromTitle, fromDangling,
#      toLevel, toNodeId, toTitle, toDangling, order }]   (dangling = the anchor node was deleted; skipped)
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/voice-rules/preview
# → [{ chapterId, chapterTitle, voiceName }] in book order — the voice at each chapter's start
#   (null = none resolves; a rule anchored inside a chapter is below this grain — see the chapter voices read in §4)
```

## 4. Generate paragraph audio

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/audio/enqueue \
  -H 'content-type: application/json' \
  -d '{ "level": "chapter", "nodeId": "<chapterId>", "needsAudioOnly": true }'
# or an explicit selection / a single retry (unknown ids ignored; enqueued = what was queued;
# 409 when no paragraph TTS service is active):
curl -s -X POST http://localhost:5000/api/projects/{folder}/audio/enqueue-items \
  -H 'content-type: application/json' \
  -d '{ "itemIds": ["<itemId>", "<itemId>"] }'
# the speech items under a node that have a speaker to read them, with their
# paragraph/chapter/part/volume ids (what an audio selection holds); needsAudioOnly=true keeps
# those still missing a WAV, narratorOnlyMode=true counts unattributed lines as readable:
curl -s 'http://localhost:5000/api/projects/{folder}/nodes/chapter/{chapterId}/item-ids?needsAudioOnly=true'
# poll /api/audio/queue; per-item:
curl -s http://localhost:5000/api/projects/{folder}/audio/items/{itemId}

# which voice each speech item of a chapter will be spoken in (null voiceName = none resolves;
# narratedBy = the linked narrator's name on narration items):
curl -s http://localhost:5000/api/projects/{folder}/nodes/chapter/{chapterId}/voices
# → { "<itemId>": { "voiceName": "Deep", "narratedBy": null }, … }

# items whose audio failed normalize/verify (sparse — absent = passed; state NeedsReview | Dismissed):
curl -s http://localhost:5000/api/projects/{folder}/audio/reviews
# → { "<itemId>": { state, normalizeOk, normalizeReason, verifyOk, wer, verifyReason,
#                   transcript, originalTextSnapshot } }
```

Enqueueing is idempotent (already-queued items dedupe) and `needsAudioOnly: true`
skips items that already have audio — safe to re-run after failures.
`POST /api/audio/cancel` clears the queue.

## 5. Book commands (edit anything)

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

## 6. Assemble the m4b

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/assembly \
  -H 'content-type: application/json' -d '{ "allowPartial": false }'
# 409 with audioRemainingCount → items still need audio (or pass allowPartial:true)
# poll /api/assembly/status (phases: Gather, Silence, ProbeConcat, Encode, Finalize)
```

Output lands at `<workspace>/{folder}/output/<book title>.m4b`
(`_partial_<date>` suffix for partial builds). Requires a valid ffmpeg path in
`/api/settings/audio-processing`.

## 7. Live hub (`/hubs/live`, SignalR)

Push channel for anything that changes without a request: queue roll-ups, per-node and per-item
status, mutation receipts, assembly / voice-batch / watchdog progress, LLM and audio-gen streams,
throughput, and settings changes. Payloads are camelCase JSON, enums as names, nulls omitted;
multi-kind families carry a `kind` discriminator. Records live in `src/Read2Me.App/Live/LiveMessages.cs`.

| Direction | Name | Notes |
|---|---|---|
| client → server | `JoinProject(folder)` / `LeaveProject(folder)` | joins `project:{folder}`; `JoinProject` returns a `ProjectSnapshot` (`revision`, `nodes`, `paragraphs`, `items`, `folderAudioRemaining`) |
| client → server | `JoinStream("llm" \| "audio")` / `LeaveStream(kind)` | joins `stream:llm` / `stream:audio`; the current in-progress turn is replayed to the caller first |
| client → server | `GetSnapshot()` | `{ queue, assembly, voiceBatch, watchdog, throughput, projects }` for the projects this connection joined |
| server → client | `queue` | `{ attribution, audio, escalation? }`, debounced 250 ms, everyone |
| server → client | `nodeStatus`, `itemStatus` | per-project deltas (null value = entry gone), debounced 250 ms, project group only |
| server → client | `receipt` | `BookMutationReceipt` with `folder` flattened and `originId` untouched, project group only |
| server → client | `assembly`, `voiceBatch`, `watchdog`, `settingsChanged` | pass-through (encode progress stepped at 1 %, batch progress debounced), everyone |
| server → client | `llm`, `audioGen` | stream groups only; LLM `delta` messages are 100 ms batches of `thinking` + `content` |
| server → client | `throughput` | `ThroughputSnapshot`, once a second while a run is active plus once when it ends |

Invalid folders and unknown stream kinds fail the invocation with a `HubException`. Node status is
only computed for folders `NodeStatusService` has seeded — call `GET /api/projects/{folder}/status`
after joining (the Angular project shell does) to seed it. Example with the .NET client:

```csharp
var conn = new HubConnectionBuilder().WithUrl("http://localhost:5000/hubs/live").Build();
conn.On<JsonElement>("receipt", r => Console.WriteLine(r));
await conn.StartAsync();
var snapshot = await conn.InvokeAsync<JsonElement>("JoinProject", "dracula");
```
