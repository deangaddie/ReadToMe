# Setup check

Before starting work, check the install: `GET /api/setup-check` answers the last report,
`POST /api/setup-check/quick` runs the Quick check and answers the new one. The Quick check also runs
once as the host starts (until it ends, `rows` is empty and `quickCheckedAt` null). It never starts,
stops or switches anything; the one thing it writes is the Workspace folder, created when missing.

A row's `id` is `workspace`, `ffmpeg` or `docker` (group `WorkspaceAndTools`); `llm`, `attribution-chain`,
`paragraph-tts`, `voice-design`, `transcription` or `similarity` (`AiConfigs`); or `container-<service>`
per compose service, e.g. `container-llama` (`DockerServices`). `tier` is `Quick | Full`; `result`
`Passed | Failed | NotConfirmed | NotRun | NotUsed` (`Checking` while a run is on the row). A `note` never
changes the result: a saved Settings ffmpeg path that hides a different appsettings `Ffmpeg:Path`, no transcription or
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
command line); every other row is `NotRun`, "needs the Workspace". Only fixing the setting and restarting
the app leaves the mode.

The AI configs rows judge each active config's base URL, and probe only a URL the app doesn't manage
(`GET /v1/models` for LLM, TTS and voice-design configs; any HTTP answer for the others). `managed` is
true on the container rows and on a config row whose every URL is a container the app manages. A
container row reads its Managed service status: Stopped is `NotConfirmed` (the Full check confirms it),
NotFound, Down, or running with nothing answering its health URL is `Failed`; one whose health URL
answers an HTTP error (llama's 503 while it loads a model) is `NotConfirmed`; a container no active config uses is
`NotUsed`. A running llama must list every `Model` in the attribution chain and the active LLM config,
and a running audiocpp every active TTS and voice-design `ModelId` (`GET /v1/models`; nothing is loaded).

**Full check.** `POST /api/setup-check/full` starts the Full check in the background and answers 202
with the report; a second request while one runs joins it (one run per host). `fullRun { step, total,
lane, label, cancelling }` says where the run is (`lane` `Local | Cpu | Gpu`; null when none runs) and
`fullCheckEstimateSeconds` how long a run is likely to take. It answers 409 naming the work in flight
while the Audio or Character queue holds items, or a voice batch, character discovery or audiobook
assembly runs — stop that first. While it runs every queue waits with the reason "Setup check running",
Preflight `plan` and `run` answer 409 with that title, as does every endpoint that drives an AI
service directly (discovery, book-edit plan/propose, voice prompt and audio generation, voice batches,
transcribe, pronunciation try-outs, the config tests, and AI service start/restart/shutdown), and the
watchdog leaves alone the services the steps drive. Each step marks its rows `Checking`; progress and the final report reach every hub
client as `setupCheck` (`Progress`, then `Done`, each with the whole `report`). `POST /api/setup-check/full/cancel` → 202 (409
when none runs) stops the run once its current step ends; the checks it hadn't reached read `NotRun`,
"Full check cancelled". Results stay in memory with `fullCheckedAt` until the app restarts, and a later
Quick check keeps a Full pass unless it now fails the row; a Full failure the Quick check no longer
finds is dropped until the next Full check. Its steps, in order (a check with no active config gets
none and stays `NotUsed`):

- `ffmpeg`: a 1 s tone through adynamicequalizer and alimiter, encoded to AAC `.m4b` by the live ffmpeg.
- `transcription` (lane `Cpu`): whisper transcribes the bundled sample clip (a Project Gutenberg
  sentence, `src/Read2Me.App/Services/SetupCheck/SampleClip/`) to non-empty text.
- `similarity` (lane `Cpu`): the transcript scored against the clip's known text; it fails below the
  config's own `PassThreshold`. With no transcript, the clip's text is scored against itself (a note says so).
- `paragraph-tts` (lane `Gpu`): a short sentence spoken with the sample clip as the reference voice; it
  passes when the live ffmpeg's ffprobe reads a duration above 0 from the audio.
- `voice-design` (lane `Gpu`): a voice designed from a fixed prompt, speaking the clip's text; the same
  pass rule.
- `llm` and `attribution-chain` (lane `Gpu`): one step per distinct `Model` in the active LLM config and
  the attribution chain, each a non-streamed chat completion with `max_tokens` 1 and thinking off; it
  passes on 200 with non-empty content. A row is `Failed` when any of its models fails.

A managed container a step uses is started when stopped, and its Docker services row gets the step's
verdict. A container fails only when it doesn't answer the request at all (or won't start); an HTTP error
it answers, such as a model that won't load, fails only the config's row, and that failure stays for the
rest of the run. Each visit to a GPU container first goes through the Preflight conflict sweep, which
stops the other GPU container if it runs, even one whose check failed (one GPU wins over "left as
found"); the watchdog leaves every GPU container alone for the run. Afterwards (finished, failed or
cancelled) each goes back as it was found, running or stopped, and llama reloads the model it had
(`status.value` `loaded` on `GET /v1/models` when the run first touched it), except a container whose
check failed, which is left as it is for its logs (or stopped, when a later sweep stopped it), and a GPU
container isn't started again while a failed one holds the card. The report's `putBack` says what
happened ("Put back: read2me-llama left running, as it was, with qwen-28b loaded again; read2me-audiocpp
stopped again."). A URL the app doesn't manage is sent the same request but never started, stopped or
put back. When audiocpp answers 500 "model path does not exist", the fix hint names the missing path and
`GGUF_MODELS_DIR` in `Infra/.env`; when llama's model "failed to load" (no path; a missing GGUF, too
little memory or a bad preset), it says to check the GGUF the `models.ini` preset names is under
`GGUF_MODELS_DIR` and to read `docker logs -f read2me-llama`. `fullCheckEstimateSeconds` reads the
containers as they are: each step's time with a model load (9–31 s, taken as 30) per GPU step, a start
for each container the run must start or restart after a sweep, and a model load for a running llama's
reload.

**Re-run one check.** `POST /api/setup-check/checks/{id}/rerun` re-runs one Full-tier row (`ffmpeg` or an
AI configs row) in the background and answers 202 with the report: only the steps that judge it run, so
`attribution-chain` exercises the chain's models alone; `similarity` transcribes first (when transcription
has a config) so it scores a real transcript. The Full check's rules hold — the same 409 naming the work in
flight, queues wait and Preflight refuses with "Setup check running", the watchdog holds off, progress goes
out as `setupCheck`, cancel works, and the containers it drives are put back. It answers 409 while a Full
check or another re-run runs (a `POST .../full` meanwhile is refused 409 too: it would check one row),
before the start-up Quick check ends, or when the row has no active config, and 404 for an unknown or
Quick-tier id. Each row's `rerun` is null when it can't be re-run, else `{ gpu }`: true when a step drives
a GPU container, so it stops the one running (the web app confirms only then). The last Quick check sets
it, so a re-run whose config has since moved onto a GPU container is refused 409 until a Quick check
refreshes it, rather than skip that confirm. `fullCheckedAt` stays the last whole Full check's.
