# Configure services (once)

Config areas: `llm`, `paragraph-tts`, `voice-design`, `transcription`,
`semantic-similarity` — same CRUD per area (`GET` list, `POST` create, `PUT /{id}`
update, `DELETE /{id}` delete — deleting the active one reassigns or clears the
selection), `PUT /active` selects (204) and `GET /active` answers the active config (404
when none). The first config created in an area auto-activates. The document marks every
config field required: answers carry them all, and a body that leaves one out gets its default.

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
`{ steps: [{ configId, thinking, promptStyle }], selfConsistency }` (`promptStyle` `Full`,
`Simple` or `Chapter`, null = inherit the config's; exact duplicate steps collapse; 422 when a step names no
config). GET adds `resolved` (the chain as attribution runs it — an empty chain falls back to the
default config) and `available` (every config). Deleting a config removes its steps.
`POST /api/settings/llm/{id}/test` with `{ prompt, connectionId }` sends a free-text test prompt
(202; 409 while one is running): tokens stream on hub group `stream:llm`, the ending arrives as
`llmTest` on `connectionId`. `POST …/{id}/test/cancel` stops it (204); `GET /api/settings/llm/test`
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
`POST /api/settings/voice-design/{id}/test` `{ prompt }` (60 s timeout),
`POST /api/settings/transcription/{id}/test` multipart `file` (wav/mp3/aac, ≤ 50 MB),
`POST /api/settings/semantic-similarity/{id}/test` `{ text1, text2 }`.

Prompt templates: `GET /api/settings/prompts` (all kinds, resolved),
`PUT /api/settings/prompts/{kind}` to override, `DELETE` to reset (both 204).
`GET /api/settings/prompts/catalog` describes every kind
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
The ffmpeg the app runs is the saved path, else the host's `Ffmpeg:Path` (appsettings, command line, env vars; read
live), else `ffmpeg` on PATH. `GET /api/settings/audio-processing` answers that resolved `ffmpegPath` with
`savedFfmpegPath` and `configuredFfmpegPath`; the full view's `ffmpegPath` is the saved value the form edits, beside
`configuredFfmpegPath`. A new `app.db` no longer copies `Ffmpeg:Path` in.
A/B preview of an unsaved step draft: `GET /api/audio/samples/recent?limit=20` lists recently generated items that still
hold a Preview Source, then
`POST /api/settings/audio-processing/steps/{stepId}/preview` with `{ sample: { folder, itemId }, settings }` answers
an original and a processed URL, both serving WAVs; `appliedOk` false means
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

Preflight — the readiness gate every AI action passes: `POST /api/preflight/{taskKind}/plan` answers
whether it is `ready`, the services to start and the conflicts to stop (task kinds `CharacterAttribution`,
`AudioGeneration`, `VoicePromptGeneration`, `CharacterDiscovery`, `VoiceDesignAudio`, `Transcription`,
`BookEdit`; 400 otherwise). Both answer 409 "Setup check running" while the Setup check's Full check runs. `POST /api/preflight/{taskKind}/run` `{ connectionId }` → 202 `{ run }` re-plans,
stops the conflicts, then starts what is missing one at a time; progress reaches that connection as
`preflight` messages: one `Stage` per service step (`WaitingToStop | Stopping | Stopped | WaitingToStart |
Starting | Ready | Failed`), then `Done` with `ok` and a `reason`. Call `plan` before an AI step and `run`
only when the user agreed — cancelling must start nothing.
