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
Inspect the result: `GET /api/projects/{folder}/book` (overview), then walk
`GET /api/projects/{folder}/nodes/{level}/{id}/children` (volume → part → chapter;
chapter children carry the paragraphs with their items).

## 2. Discover characters, attribute dialog

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/discover
# review the rows, then persist the ones you keep:
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/discover/apply \
  -H 'content-type: application/json' \
  -d '[ { "name": "Alice", "aliases": ["Al"] } ]'

# queue attribution per chapter (or part/volume):
curl -s -X POST http://localhost:5000/api/projects/{folder}/attribution/enqueue \
  -H 'content-type: application/json' \
  -d '{ "level": "chapter", "nodeId": "<chapterId>", "unprocessedOnly": true }'
# poll /api/attribution/queue; per-paragraph result:
curl -s http://localhost:5000/api/projects/{folder}/attribution/paragraphs/{paragraphId}
```

Manual fixes go through the generic commands endpoint (section 5), e.g.
`SetParagraphCharacter`.

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
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio
```

## 4. Generate paragraph audio

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/audio/enqueue \
  -H 'content-type: application/json' \
  -d '{ "level": "chapter", "nodeId": "<chapterId>", "needsAudioOnly": true }'
# poll /api/audio/queue; per-item:
curl -s http://localhost:5000/api/projects/{folder}/audio/items/{itemId}
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
