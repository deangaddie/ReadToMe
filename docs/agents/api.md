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
`semantic-similarity` — same CRUD per area (`GET` list, `POST` create, `PUT /{id}`
update, `DELETE /{id}` delete — deleting the active one reassigns or clears the
selection), `PUT /active` selects and `GET /active` answers the active config (404
when none). The first config created in an area auto-activates.

```bash
curl -s http://localhost:5000/api/settings/llm                       # list
curl -s -X POST http://localhost:5000/api/settings/llm \
  -H 'content-type: application/json' \
  -d '{ "name": "local llama", "baseUrl": "http://localhost:8080" }'
curl -s -X PUT http://localhost:5000/api/settings/llm/active \
  -H 'content-type: application/json' -d '{ "id": 1 }'
```

LLM extras: `POST /api/settings/llm/models` takes an LLM config (saved or not) and answers
`{ models }`, or 422 with the reason when the server cannot be asked.
`GET/PUT /api/settings/llm/attribution-chain` reads and replaces the attribution escalation chain —
`{ steps: [{ configId, thinking, promptStyle }], selfConsistency }` (`promptStyle` 0 = Full,
1 = Simple, null = inherit the config's; exact duplicate steps collapse; 422 when a step names no
config). GET adds `resolved` (the chain as attribution runs it — an empty chain falls back to the
default config) and `available` (every config). Deleting a config removes its steps.
`POST /api/settings/llm/{id}/test` with `{ prompt, connectionId }` sends a free-text test prompt
(202; 409 while one is running): tokens stream on hub group `stream:llm`, the ending arrives as
`llmTest` on `connectionId`. `POST …/{id}/test/cancel` stops it; `GET /api/settings/llm/test`
answers `{ running, configId }`.

Provider extras (`paragraph-tts`, `voice-design`, `transcription`, `semantic-similarity`): a config's
provider settings live in `settingsJson`, and every create / update rewrites it into the provider
record's own key case and order — send the keys in any case; text that is not the provider's shape
is 400. `GET /api/settings/{area}/schema?type=` lists a provider type's editable fields beyond its
base URL (none for transcription, `PassThreshold` for similarity).
`GET /api/settings/paragraph-tts/{id}/text-steps` → `[{ stepId, label, description, builtIn, options? }]`:
the ids a TTS config may put in `enabledStepIds` (built-ins, then its own `substitutionSteps`; id 0 =
built-ins only). `GET/PUT /api/settings/voice-design/sample-text` → `{ text, default, maxLength }` (`text` null =
default; PUT `{ text }`, null or the default text clears it; `maxLength` 300, longer is 400).
Test one stored config — a provider that is down answers 422 with its reason:
`POST /api/settings/voice-design/{id}/test` `{ prompt }` → `{ audioBase64, contentType }` (60 s timeout),
`POST /api/settings/transcription/{id}/test` multipart `file` (wav/mp3/aac, ≤ 50 MB) → `{ transcript }`,
`POST /api/settings/semantic-similarity/{id}/test` `{ text1, text2 }` → `{ score, threshold, pass }`.

Prompt templates: `GET /api/settings/prompts` (all kinds, resolved),
`PUT /api/settings/prompts/{kind}` to override, `DELETE` to reset.
`GET /api/settings/prompts/catalog` describes every kind —
`{ kind, title, description, tokens[], expectedResponse, template, defaultTemplate, isOverridden, warnings[] }`
(`template` is the resolved text, `isOverridden` says it differs from the built-in default,
`warnings` carries the compatibility problems the settings pages show, e.g. a Full attribution override
without `{{narrator_identity}}`). `POST /api/settings/prompts/{kind}/preview` with `{ template }` answers
`{ rendered }`: the given text (saved or not) filled with the kind's sample book, the same values both UIs preview with.
Audio post-processing scalars: `GET/PUT /api/settings/audio-processing`. The settings page's full view is
`GET /api/settings/audio-processing/full` (scalars + `pauses { volumeMs, partMs, chapterMs, paragraphMs, pauseMs }` +
`steps[]`, the paragraph post-process step configs `{ stepId, enabled, settings }` in pipeline order: `silence-trim`
`{ thresholdDb, padMs, minOutputMs }`, `consonant-soften` `{ engine, preset, adynEq?, deesser? }`).
`PUT /api/settings/audio-processing/pauses` saves the five pauses together (400 on a negative);
`PUT /api/settings/audio-processing/steps/{stepId}` upserts one step (400 for a step outside the paragraph pipeline).
`POST /api/settings/audio-processing/ffmpeg/test` with `{ ffmpegPath? }` persists the path, then probes it → `{ success, message }`.
A/B preview of an unsaved step draft: `GET /api/audio/samples/recent?limit=20` lists recently generated items that still
hold a Preview Source (`{ itemId, folder, text, characterName, voiceName, projectTitle }`), then
`POST /api/settings/audio-processing/steps/{stepId}/preview` with `{ sample: { folder, itemId }, settings }` answers
`{ previewId, originalUrl, processedUrl, removedMs?, reason?, appliedOk }` — both URLs serve WAVs; `appliedOk` false means
the step fell back and the processed side is the unprocessed audio (404 unknown folder, 422 evicted sample).
Themes (shared with both UIs): `GET/POST /api/settings/themes`, `PUT/DELETE /api/settings/themes/{id}`
(built-in rows are read-only → 400), `GET/PUT /api/settings/themes/selection`
(`{ selectedThemeId, followSystemPreference }`, both optional on PUT).

Container health and lifecycle: `GET /api/ai-services`, `GET /api/ai-services/status` (every service,
one probe each), `GET /api/ai-services/{name}/status`, and `GET /api/ai-services/resolve?baseUrl=` for the
managed service behind a config's base URL (404 when it is not one of ours).
`POST /api/ai-services/{name}/start | restart | shutdown` answer 202 (409 while that service already has an
op in flight); the outcome arrives on `/hubs/live` as `serviceStatus { name, status, op, ok, error? }`, which
every probe and watchdog transition also emits. Remember the GPU fits one model at a time — start only the
containers the current step needs.

Preflight — the readiness gate every AI action passes: `POST /api/preflight/{taskKind}/plan` →
`{ ready, toStart: [{ name, status }], conflicts: [{ name, reason }] }` (task kinds `CharacterAttribution`,
`AudioGeneration`, `VoicePromptGeneration`, `CharacterDiscovery`, `VoiceDesignAudio`, `Transcription`,
`BookEdit`; 400 otherwise). `POST /api/preflight/{taskKind}/run` `{ connectionId }` → 202 `{ run }` re-plans,
stops the conflicts, then starts what is missing one at a time; progress reaches that connection as
`preflight { kind: stage, run, name, stage, error? }` (`waitingToStop | stopping | stopped | waitingToStart |
starting | ready | failed`) then `{ kind: done, run, ok, reason? }`. Call `plan` before an AI step and `run`
only when the user agreed — cancelling must start nothing.

### Setup check

Before starting work, check the install: `GET /api/setup-check` answers the last report,
`POST /api/setup-check/quick` runs the Quick check and answers the new one. The Quick check also runs
once as the host starts (until it ends, `rows` is empty and `quickCheckedAt` null). It never starts,
stops or switches anything; the one thing it writes is the Workspace folder, created when missing.

```bash
curl -s -X POST http://localhost:5000/api/setup-check/quick
# → { rows: [{ id, group, name, tier, result, expected, found, note, fix: { text, link: { path, label }?, copy? }, managed }],
#     quickCheckedAt, fullCheckedAt, firstRun, startedAt, noWorkspace }
```

`id` is `workspace`, `ffmpeg` or `docker` (Workspace and tools); `llm`, `attribution-chain`,
`paragraph-tts`, `voice-design`, `transcription` or `similarity` (AI configs); or `container-<service>`
per compose service, e.g. `container-llama` (Docker services). `group` is
`workspaceAndTools | aiConfigs | dockerServices`; `tier` `quick | full`; `result`
`passed | failed | notConfirmed | notRun | notUsed` (`checking` while a run is on the row). A `note` never
changes the result: an appsettings `Ffmpeg:Path` that differs from the Settings value, no transcription or
similarity config ("audio is generated without an accuracy score"), a URL the app doesn't manage, or
`AiWatchdog:Enabled = false` on the container rows. `fix.link.path` is the web app's Settings page that
owns the value (`settings/audio`, `settings/llm`, …); `fix.copy` is a command or file path for things
outside the app (`docker compose up -d llama`, `Infra/llama/config/models.ini`). `firstRun` is true on
the start that created `app.db` (the web app then lands on the Setup check); `startedAt` is when this
host started.

**No Workspace.** When `Workspace:FolderPath` is empty, relative, a file, or a folder that can't be created
or written, the host still starts, without `app.db`: no migrations, no queue workers or History tracker, and
`noWorkspace` is true. Every other `/api/...` call (and `/workspace/...`, `/audio-preview/...`,
`/preview-source/...`) answers `503` with the problem title `No Workspace`. The `workspace` row fails with a
fix hint naming `Workspace:FolderPath`, the absolute path of `appsettings.Local.json` and the source that
takes effect (that file, another settings file, the `Workspace__FolderPath` environment variable or the
command line); every other row is `notRun`, "needs the Workspace". Only fixing the setting and restarting
the app leaves the mode.

The AI configs rows judge each active config's base URL, and probe only a URL the app doesn't manage
(`GET /v1/models` for LLM, TTS and voice-design configs; any HTTP answer for the others). `managed` is
true on the container rows and on a config row whose every URL is a container the app manages. A
container row reads its Managed service status: Stopped is `notConfirmed` (the Full check confirms it),
NotFound, Down, or running with nothing answering its health URL is `failed`; one whose health URL
answers an HTTP error (llama's 503 while it loads a model) is `notConfirmed`; a container no active config uses is
`notUsed`. A running llama must list every `Model` in the attribution chain and the active LLM config,
and a running audiocpp every active TTS and voice-design `ModelId` (`GET /v1/models`; nothing is loaded).

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
its `structureRules` are the **Structure rules** described under `InsertNode` below), then walk
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

curl -s -X DELETE http://localhost:5000/api/projects/{folder}          # → 204; the folder and everything in it
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
#   review, attributionProcessing, attributionQueued, isDone } },
#   pauseParagraphs,                                      ← paragraphs: no items, or one pause item
#   checkpoints: { <step id>: { state, stale, at } }, revision }
curl -s http://localhost:5000/api/projects/{folder}/nodes/chapter/{chapterId}/status   # one node's summary
```

`characters` excludes the seed Narrator row; `charactersWithLines` counts speakers (narration
included, credited to the linked narrator when there is one) and `readyVoices` how many of them
have a voice with audio. Both reads reseed the node roll-ups from the database, so they are
current even with no UI open, and hub clients receive the corrected `nodeStatus` deltas.

A Pipeline step the user has checked holds a **checkpoint**. `checkpoints` lists only the steps
that have one, keyed by the camelCase step id (`import | structure | speakers | text | voices |
titles | pauses | audio | export`). Unlike the PascalCase enum strings elsewhere in this API (an
enum without a converter, such as a settings config's `type` or a queue `outcome.kind`, goes out
as its integer), a checkpoint's `state` is lower case too (`confirmed` | `skipped`), because these ids double as the
route segment and the web's step ids. Set, replace or clear one:

```bash
curl -s -X PUT http://localhost:5000/api/projects/{folder}/checkpoints/structure \
  -H 'Content-Type: application/json' -d '{"state":"confirmed"}'   # confirmed | skipped | open → 204
```

Structure, Text and Titles take `confirmed` or `skipped`, Speakers only `confirmed` and Pauses
only `skipped`. The other steps are done by their data, so any state on them is a 422, `open`
included. `open` deletes the row. Repeating the state a row already holds changes nothing and
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
curl -s http://localhost:5000/api/projects/{folder}/characters            # the plain roster: id, name, aliases
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
# → { "paragraphs": 72, "itemsWithAudio": 12,
#     "speechMarkers": [ { "marker": "A.", "paragraphs": 36 }, { "marker": "Q.", "paragraphs": 36 } ] }
curl -s -X POST http://localhost:5000/api/projects/{folder}/commands \
  -H 'content-type: application/json' -d '{ "type": "ReleaseNarration", "scope": "Chapter", "nodeId": "<chapterId>" }'
```

`paragraphs` is what the release would write and `itemsWithAudio` the audio it would discard.
`speechMarkers` names the markers that passed, keyed as written (`Q.` and `Q:` are separate;
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
# → { "newEntityId": "<character id>" }
```

Unlike the single-step speaker commands it rejects rather than no-ops: a blank name, no target or
more than one, or an empty `paragraphIds` is **422** with nothing written; an unknown target is
**422** too, though the character has been created by then.

## 3. Voices

```bash
# plan voices + design prompts for every character (one LLM call each):
curl -s -X POST http://localhost:5000/api/projects/{folder}/voice-batch/prompts \
  -H 'content-type: application/json' -d '{ "regenerateAll": false }'
# poll /api/voice-batch/status until isRunning=false

# synthesise reference audio for every planned voice:
curl -s -X POST http://localhost:5000/api/projects/{folder}/voice-batch/audio
# poll /api/voice-batch/status; stop a running batch early (voices done so far stay):
curl -s -X POST http://localhost:5000/api/voice-batch/cancel

# inspect / regenerate one voice:
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices
curl -s http://localhost:5000/api/projects/{folder}/voices/{voiceId}
# → { id, characterId, name, description, source: "Uploaded"|"Generated", designPrompt, transcript, audioFileName,
#     isEdited, voiceDesignSettingsOverrideJson, ttsSettingsOverrideJson,
#     referenceSeconds (from the stored WAV header), referenceWarning (set over the 15 s soft Reference Limit) }
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio # 503 "TTS busy, try again" = retry later
# → { audioFileName, transcript, referenceSeconds, referenceWarning } (warned over 15 s); 422 over the 30 s hard
# Reference Limit, nothing stored — shorten the voice-design sample text
# with an unsaved prompt: designs from it instead of the stored one and stores it with the take, so the
# prompt edit and its audio are one request (a failed take stores neither); no body uses the stored prompt
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio \
  -H 'content-type: application/json' -d '{ "designPrompt": "A gravelly old voice, slow and dry." }'

# reference audio: upload/replace (multipart 'file', 200 MB max; normalised + committed; 422 over the 30 s / 5 MiB
# hard Reference Limit with nothing stored — never trimmed), then transcribe:
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
curl -s 'http://localhost:5000/api/settings/paragraph-tts/schema?type=VoxCpm2'   # VoxCpm2|Chatterbox|Qwen3Base|Breeze
curl -s 'http://localhost:5000/api/settings/voice-design/schema?type=Qwen3'      # VoxCpm2|Qwen3|Breeze
# → { type, fields: [{ key, label, kind: number|boolean|enum|string|text, min?, max?, step?, options?, default, help?, nullable }] }
# then: SetVoiceTtsSettingsOverride / SetVoiceSettingsOverride commands with json = '{"cfg_value":3.5}' (null clears)

# voice audio editor: clean up a voice's reference audio with post-process steps. The catalog lists
# the voice-scope steps in chain order with their dials (settings-form fields) and defaults; preview
# renders the steps you name (any order — the host orders them) over the voice's ORIGINAL audio and
# answers one playable stage per step; apply names the previewId so what is written is what was heard.
curl -s 'http://localhost:5000/api/audio/steps/catalog?scope=voice'
# → [{ stepId, label, blurb, dials: [<settings-form fields>], defaults }]   (de-plosive, denoise, hiss-reduce, consonant-soften, silence-trim)
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/editor/preview \
  -H 'content-type: application/json' \
  -d '{ "steps": [ { "stepId": "denoise", "settings": { "strength": 30 } }, { "stepId": "silence-trim", "settings": { "thresholdDb": -40, "padMs": 100 } } ] }'
# → { previewId, stages: [{ stepId, applied, reason?, url }] }   url = /api/previews/{previewId}/{stepId}.wav, valid 30 min
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/editor/apply \
  -H 'content-type: application/json' -d '{ "previewId": "<from preview>" }'      # → VoiceDto with isEdited=true; 422 if expired / another voice's
curl -s http://localhost:5000/api/projects/{folder}/voices/{voiceId}/original.wav  # the pre-edit audio (404 while unedited)
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/editor/restore   # → VoiceDto, original back, edit forgotten

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

### Pronunciation list

A book's **Pronunciations** respell made-up words for TTS: wherever an item's text has the written
form, TTS is asked to say the spoken form (the item's **Spoken text**); the book text never changes.
Whole words, any case (the match's case carries over to the spoken form), the longer written form
wins where two overlap. Verify scores the transcript against the text and the spoken text and keeps
the lower WER.

```bash
curl -s http://localhost:5000/api/projects/{folder}/pronunciations          # honours ?at=<stepId>
# → [{ id, writtenForm, spokenForm, matchCount, itemCount, withAudioCount }]  sorted by written form
curl -s 'http://localhost:5000/api/projects/{folder}/pronunciations/matches?chapterIds=…&chapterIds=…'  # honours ?at=
# → { pronunciations: [{ id, writtenForm, spokenForm }], spans: [{ itemId, start, length, pronunciationId, sentenceStart }] }
# The Book view's marks: spans in book order in the named chapters, offsets into the item's text; the
# entries they name, sorted by written form. Unknown chapters match nothing.
curl -s -X POST http://localhost:5000/api/projects/{folder}/pronunciations \
  -H 'content-type: application/json' -d '{ "writtenForm": "Watson", "spokenForm": "WAT-sun" }'
# → 201 with the entry plus audioDiscardCount and needsAudioCount. Both forms trimmed, inner whitespace
# collapsed. 400: written form not 1–100 characters or not starting and ending with a letter or digit,
# spoken form not 1–200 characters; 409: another entry has that written form, ignoring case.
# One History step: Pronunciation: add "Watson" (action pronunciation).
curl -s -X PUT http://localhost:5000/api/projects/{folder}/pronunciations/{id} \
  -H 'content-type: application/json' -d '{ "writtenForm": "Watson", "spokenForm": "WOT-sun" }'
# → 200, same answer and refusals as the add; 404 unknown id. Unchanged forms write nothing, no step.
# Step: Pronunciation: edit "Watson" (action pronunciation).
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/pronunciations/{id}
# → { audioDiscardCount, needsAudioCount }; 404 unknown id. Step: Pronunciation: delete "Watson" (action delete).
curl -s -X POST http://localhost:5000/api/projects/{folder}/pronunciations/impact \
  -H 'content-type: application/json' -d '{ "id": null, "writtenForm": "Watson", "spokenForm": "WAT-sun", "delete": false }'
# → { matchCount, itemCount, withAudioCount, audioDiscardCount, overlaps: [{ id, writtenForm, spokenForm, draftWins }],
#     duplicateOf, matches: [{ itemId, chapterId, chapterTitle, speaker, hasAudio, sentence, sentenceStart,
#     matchStart, matchLength, spokenSentence, spokenMatchStart, spokenMatchLength }] }  A dry run: no step; audioDiscardCount is what saving the draft discards.
curl -s -X POST http://localhost:5000/api/projects/{folder}/pronunciations/try -o try.wav \
  -H 'content-type: application/json' \
  -d '{ "itemId": "…", "sentenceStart": 9, "draft": { "id": null, "writtenForm": "Watson", "spokenForm": "WAHT-sun" },
        "asWritten": false, "voice": { "voiceId": "…" } }'
# → the WAV body (audio/wav): the item's sentence starting at sentenceStart (an impact match's), with the list
# (the draft in its entry's place) applied unless asWritten, in the voice named or "item" (the item's own
# resolved voice). Active TTS config and the item's delivery cue, as for its take. Nothing stored, no step.
# 400 no sentence starts there / bad draft or voice; 404 unknown item or voice; 409 "Can't play while
# Attribution is running: it has the GPU." while attribution, audio generation or a voice batch is queued or
# running in any project; 422 no voice resolves, it has no audio or no TTS config is active; 503 TTS busy or down.
```

Any change to the list discards the audio (WAV reference and audio review) of every item that has
audio and whose Spoken text it alters, as a text edit does: new matches on an add, the entry's
matches on a spoken-form edit, old and new matches on a written-form edit, items where an overlap's
winner changes what is said, the old matches on a delete. Items without audio change silently. Add,
edit and delete answer 409 while audio generation is queued or running for the project (a take in
flight would land with the old Spoken text). The receipt carries the `Pronunciations` facet, so
open pages refetch the list.

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

### Edit with AI

Say what to change in plain language and let the LLM write the edits. Four steps: plan (what the
instruction means and what it matches), propose (a new value per matched item), review (yours to
correct), apply (one `ApplyBookEdits` command). The plan stays on the host under the opaque
`program` id — you hold ids and values, never the plan. A session expires two hours after its last
use; `DELETE` it when you are done. Titles and paragraph text only.

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/book-edits/plan \
  -H 'content-type: application/json' \
  -d '{ "instruction": "rename every chapter to Chapter {n}", "thinking": false }'
# → { status, reason, summary, program, transform, targetCount, requestCount, warnings[] }
# status Ok carries the program; NoLlmConfigured | Unsupported | ServiceUnavailable | Failed |
# NoTargets explain themselves in reason instead. transform Llm means one LLM request per 8 items
# (requestCount says how many) and per-row retries; RegexReplace / SetTemplate / ChangeCase run in code.
# RegexReplace, and ChangeCase with a pattern, count only the items their pattern matches (none → NoTargets);
# a paragraph-text ChangeCase warns "N titles also match — run again targeting <levels> titles" when titles match too.
# 409 "Project is restoring" while a Restore runs, and also when one began after the plan was asked for:
# the plan's targets would predate it, so plan again once it has finished. A web client also drops an answered
# program whose bookEdit dropped message reached the hub first.

# start the run (202) and follow it on the hub as bookEdit { kind: progress | done | failed } —
# pass the connectionId you want it pushed to, or leave it out and poll the session instead:
curl -s -X POST http://localhost:5000/api/projects/{folder}/book-edits/{program}/propose \
  -H 'content-type: application/json' -d '{ "thinking": false, "connectionId": null }'
curl -s http://localhost:5000/api/projects/{folder}/book-edits/{program}
# → { status: Idle|Running|Completed|Cancelled|Failed, done, total, reason,
#     rows: [{ kind, id, displayPath, oldValue, newValue, status, failureReason }] }
# row status: Proposed (usable) | NoChange (the value equals the current text) | Failed

# stop early — the rows already computed stay reviewable and appliable:
curl -s -X POST http://localhost:5000/api/projects/{folder}/book-edits/{program}/cancel

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

## 6. Assemble the m4b

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/assembly \
  -H 'content-type: application/json' -d '{ "allowPartial": false }'
# 409 with audioRemainingCount → items still need audio (or pass allowPartial:true)
# poll /api/assembly/status (phases: Gather, Silence, ProbeConcat, Encode, Finalize);
# it names the job's folder, and outputFileName once the job completed
curl -s -X POST http://localhost:5000/api/assembly/cancel      # stop the running assembly (isRunning → false; the hub sends AssemblyCancelled)

curl -s http://localhost:5000/api/projects/{folder}/assembly/outputs
# → [{ fileName, sizeBytes, createdAt, isPartial }], newest first
curl -s -OJ http://localhost:5000/api/projects/{folder}/assembly/outputs/{fileName}   # audio/mp4 attachment, Range supported
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/assembly/outputs/{fileName}   # 204
```

`{fileName}` must be one of the listed `.m4b` names; anything else — a path, a `.tmp` encode in
flight, a missing file — is a 404.

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
| client → server | `JoinProject(folder)` / `LeaveProject(folder)` | joins `project:{folder}`; `JoinProject` returns a `ProjectSnapshot` (`folder`, `revision`, `nodes`, `paragraphs`, `items`, `folderAudioRemaining`) |
| client → server | `JoinStream("llm" \| "audio")` / `LeaveStream(kind)` | joins `stream:llm` / `stream:audio`; the current in-progress turn is replayed to the caller first |
| client → server | `GetSnapshot()` | `{ queue, assembly, voiceBatch, watchdog, serviceStatus, throughput, projects }` for the projects this connection joined |
| server → client | `queue` | `{ attribution, audio, escalation? }`, debounced 250 ms, everyone |
| server → client | `nodeStatus`, `itemStatus` | per-project deltas (null value = entry gone), debounced 250 ms, project group only |
| server → client | `receipt` | `BookMutationReceipt` with `folder` flattened and `originId` untouched, project group only |
| server → client | `history` | a History step (`stepLanded` when it appears, `stepChanged` when a later commit extends it while open, `stepClosed` when it closes or its note changes once closed) with `folder` and the `step` row as `GET /history` lists it, project group only. A step recorded with no request or run open lands and closes together; a request's or Background run's step lands open and closes when it ends. `busyChanged` carries no step: the project's busy `reason` moved ("Attribution: 128 paragraphs left"), or went idle, when `reason` is omitted. Only folders someone has joined get it, and only on a change; the `GET /history` list carries the busy answer for a fresh reader. `restoring` brackets a Restore: `phase` `started`, then `finished` with the Restore `step` it added (none when it added none or failed) |
| server → client | `bookEdit` (`dropped`) | a Restore dropped an Edit with AI session: `program` is its id, `reason` "The project was restored — run it again"; project group, unlike the run's other `bookEdit` messages, which go to the connection that started it |
| server → client | `assembly`, `voiceBatch`, `watchdog`, `settingsChanged` | pass-through (encode progress stepped at 1 %, batch progress debounced), everyone. Every `assembly` message names the run's `folder`; `completed` carries `outputFileName`. The snapshot's `assembly.encodePercent` is 0–100 (the REST status keeps the 0–1 fraction) |
| server → client | `llm`, `audioGen` | stream groups only; LLM `delta` messages are 100 ms batches of `thinking` + `content` |
| server → client | `throughput` | `ThroughputSnapshot`, once a second while a run is active plus once when it ends |
| server → client | `llmTest` | how an LLM settings test send ended (`done`, `failed` with `reason`, `cancelled`), sent only to the `connectionId` that started it; the tokens are on `stream:llm` |
| server → client | `bookEdit` | one AI book-edit proposal run (`progress`, `done`, `failed`), sent only to the `connectionId` that started it; `done` carries every row it landed, and `cancelled` says whether a cancel cut it short |
| server → client | `serviceStatus` | `{ name, status, op?, ok?, error? }` — a managed service's last observed `AiServiceStatus`, after every probe, lifecycle op (then `op` names it with its outcome) and watchdog transition; everyone |
| server → client | `preflight` | one pre-flight run (`stage` with `name` + `stage` + `error?`, then `done` with `ok` + `reason?`), sent only to the `connectionId` that started it; every message carries the `run` id the 202 answered |

Invalid folders and unknown stream kinds fail the invocation with a `HubException`. Node status is
only computed for folders `NodeStatusService` has seeded — call `GET /api/projects/{folder}/status`
after joining (the web project shell does) to seed it. Example with the .NET client:

```csharp
var conn = new HubConnectionBuilder().WithUrl("http://localhost:5000/hubs/live").Build();
conn.On<JsonElement>("receipt", r => Console.WriteLine(r));
await conn.StartAsync();
var snapshot = await conn.InvokeAsync<JsonElement>("JoinProject", "dracula");
```

## 8. History

Every change to a project's **Project state** is recorded as a **History step** in the same
transaction as the change (ADR 0016). Results and progress (generated audio, audio reviews,
transcription scores, Pipeline checkpoints) never make a step, and nor does a write that changed
nothing.

One request is at most one step, whatever it commits: every write endpoint under
`/api/projects/{folder}` opens a **step scope** for its request, labelled in user terms ("Edit with
AI", "Split chapter", "New character", "Upload voice audio"; a `commands` request is labelled for
its command). An agent's requests are grouped exactly as the web app's are. A request that commits
nothing leaves no step.

A **Background run** is one step too: from the moment the Character Queue or the voice batch makes
the project busy until both have drained, been cancelled or failed, everything they commit
(including characters and aliases attribution creates for itself) is one step labelled
"Attribution", "Voice batch" or, on a step both wrote to, "Attribution and voice batch". Each step is
labelled from the work that landed in it, so a part holding only voice work says "Voice batch". A Background run that
changed nothing leaves no step, and results it commits never split it. Any other write committed
outside a request is a step of its own, labelled from the mutation.

Whoever is interrupted is split; nothing is merged. A request that commits while a Background run's step is
open closes it, so the Background run carries on in a new step: Attribution "part 1", the request's step,
Attribution "part 2". A Background run's commit landing in the middle of a multi-commit request splits the
request the same way.

```bash
curl -s http://localhost:5000/api/projects/{folder}/history
# → { steps: [{ stepId, parentStepId, sequence, label, summary, author, startedAt, endedAt, isOpen, note,
#               kind, action, restoredFromStepId, cantOpen }],
#     headStepId, busy: { reason } | null, restoring }
```

- `steps` are newest first. `sequence` is for display; the line is the `parentStepId` chain from
  `headStepId`, the tip.
- `summary` is what changed, in user terms: "4 paragraphs in Chapter 3", "12 paragraphs across 3
  chapters", or a title ("The Storm"). Null when the step changed nothing it can count or place, such
  as a roster or Voice change.
- `endedAt` is when the step closed or, while it is open, its last change, so every step has a time
  span; an interrupted step keeps the time of its last change. `isOpen`, not `endedAt`, says whether
  it is open.
- `isOpen` is true while the request or Background run that owns the step is still running;
  `stepLanded` then `stepClosed` on the live hub bracket it, with a `stepChanged` for each later
  commit in between.
- `note` says what is unusual about the step, or is null: "part 1", "part 2" for a split step, how a Background
  run ended early ("212 of 340 paragraphs, cancelled", "3 of 7 voices, stopped on an error") or with items that
  failed on their own ("3 paragraphs failed", "212 of 340 paragraphs, cancelled · 3 failed",
  "3 of 7 voices, stopped on an error · 2 failed"), and
  "interrupted" for a step the app stopped with open, closed on the next start. Pieces join with
  " · " ("part 2 · interrupted") and carry no brackets: a client shows one as "Attribution (part 2 ·
  interrupted)". A step whose note is set
  after it closed ("part 1", once part 2 lands) is sent again as `stepClosed`.
- `kind` says what opened the step: `request`, `backgroundRun`, `restore`, `started` (the "History
  started" step) or `import` (an import or reread of the book). `action` says what kind of action a
  `request` or `backgroundRun` step is: `edit`, `editWithAi`, `insert`, `split`, `merge`, `delete`,
  `title`, `pause`, `character`, `voice` (a voice batch alone, too), `pronunciation` or `attribution` (a Background
  run with attribution in it); null on the other kinds. A write committed outside any request or Background run is
  a `request` step whose action is `edit`. Tell steps apart by these, never by `label`:
  labels are wording and may change. The hub's `stepLanded`, `stepChanged` and `stepClosed` carry
  them too (with null fields omitted).
- `busy` names what is queued or running for the project ("Attribution: 128 paragraphs left"), or
  is null when it is idle. `restoring` is true while a Restore of the project runs.
- History starts the first time a project is opened (`GET /api/projects/{folder}` or this call) or
  written. An existing project starts with a "History started" step; a new one starts at its
  import step.
- 404 for an unknown project.

### Viewing a step: `?at=`

Every project read of Project state accepts `?at=<stepId>` and answers exactly as it would have
right after that step for the Book, characters and aliases, Voices, Voice Rules, the Pronunciation
list and project metadata.
Nothing else about the call changes, so an agent inspects the past with the calls it already uses.

```bash
curl -s http://localhost:5000/api/projects/{folder}/history/{stepId}
# → the step, as GET /history lists it, plus restorePreview: { audioDiscardCount } (null for a cantOpen step);
#   404 unknown step, 409 while the project is busy or restoring, 503 when it failed to open for now

curl -s "http://localhost:5000/api/projects/{folder}/characters?at={stepId}"
```

- `?at=` reads: `GET /api/projects/{folder}`, `/status`, `/book`, `/characters`,
  `/characters/summary`, `/characters/{id}/lines`, `/characters/{id}/voices`,
  `/characters/{id}/voice-rules` (and `/preview`), `/voices/{voiceId}`,
  `/paragraphs/{id}/context`, `/pronunciations` (and `/matches`) and the `/nodes/...` reads. The OpenAPI document lists `at` on each.
- 404 for a step the project's History doesn't have, 409 while anything is queued or running for
  the project (`detail` carries the reason, as `busy.reason` does), 422 for a step marked
  `cantOpen`, 400 when `at` is not a step id. A step recorded before a schema upgrade is migrated
  as it opens. One whose chunks fail to replay (a damaged patchset), or whose old schema fails to
  migrate, is marked `cantOpen` for good: the read that found out answers 422, and the hub sends
  `stepChanged` with the row. A failure that passes (an IO error, a full disk, a busy database)
  answers 503 "try again" and marks nothing; reading the step again may work. There is no retry
  action: nothing clears `cantOpen`.
- State files read at a step too: `/workspace/{folder}/{path}?at={stepId}` serves a Voice's
  reference audio, the cover or the source book file as it was after that step, and
  `/voices/{voiceId}/original.wav?at=` the voice editor's original. 404 when the file didn't
  exist then (an item's take is a result, so it never exists at a step), with the same 400/404/409
  as the other `?at=` reads. Without `?at=` the live file is served as before.
- Reads of live results, progress or scratch refuse `?at=` with 400, saying why: `/history`, audio
  reviews, queue status, Edit with AI runs, Assembly outputs and `/preview-source`. A Voice's
  `referenceSeconds` is still read from its live WAV. `revision` on `/book` and `/status` is the
  current one, not the step's.
- Writes only ever go to the current project: any write with `?at=` answers 400 and writes nothing.
- At a step, `/status` reports nothing in flight and no Pipeline checkpoints or reviews, since
  results and progress are not History. For the same reason an item's `audioFileName` at a step
  is not a record of its audio then: generating audio never makes a step. The live project stays
  writable while a step is read.

### Restore

**Restore** makes the project as it stood after a step current, as a **new** step. Every later step
stays in History, so nothing is lost: a Restore can itself be restored over, and Undo restore is a
Restore to the step that was the tip before.

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/history/{stepId}/restore
# → { step, audioDiscardCount, needsAudioCount }; 404 unknown step, 409 busy or restoring (detail says why), 422 cantOpen,
#   503 when opening the step failed for now (try again)
```

- The project changes exactly as the equivalent hand edits would. Records keep their original ids,
  so Voice Rules and other links to them still hold. Items whose text, speaker or delivery cue
  changes lose their audio and its review; items in both states that didn't change keep their
  current audio; items brought back return without audio. The Pipeline checkpoints it disturbs go
  stale as the equivalent edits would stale them (Structure, Titles and Pauses for a changed Volume,
  Part or Chapter; Text for changed item text).
- Voices, their reference audio (and the voice editor's original), the cover and the source book
  file come back as they were. App-wide LLM and TTS settings are not part of a project and are
  untouched.
- `step` is the Restore step: labelled "Restore to 'Split chapter' (14:02)" (the step's label and
  local time), `restoredFromStepId` the step brought back, and `summary` the audio it cost
  ("Discarded audio on 60 items", null when none). It lands and closes on the hub like any step.
  `step` is null when the project already was that state: nothing changed, and no step is added.
- `audioDiscardCount` is the items whose audio the Restore discarded; `needsAudioCount` is every
  spoken item without audio after it, which adds the items brought back and any that had none
  already: what needs generating. Pauses never count. Both are 0 when `step` is null. `step.parentStepId` is the tip before the
  Restore, the step Undo restore restores.
- `GET /history/{stepId}` answers `restorePreview.audioDiscardCount` beforehand, so a client can say
  what a Restore costs before the click; the Restore counts again as it applies.
- Refused with 409 while anything is queued or running for the project (`detail` is the busy
  reason) or a Restore of it is already running. While one runs, every other write and enqueue for
  the project answers 409 "Project is restoring" at once, and `GET /history` says `restoring: true`.
- Open Edit with AI sessions for the project are dropped: they were planned against the project as
  it was, so their apply can't write over the restored text blind. Their dialogs hear it on the hub
  (`bookEdit` `dropped`).
- The receipt is whole-project, so every open view rebuilds.
