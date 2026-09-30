# ReadToMe

Converts text/epub book files into audiobooks via AI-powered character attribution and TTS synthesis.

## Requirements

- **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0>
- **Docker** (with NVIDIA GPU support) — for AI inference services
- **ffmpeg** — for audio normalisation and m4b assembly (path configured in app settings)
- **RTX 3070 or similar** — 8 GB VRAM minimum for GPU containers
- **32 GB RAM**, with the Docker WSL2 VM set to `memory=16GB` in `.wslconfig` — llama keeps the offloaded model layers locked in the VM (see [Infra/README.md](Infra/README.md#host--wsl-memory))

## Build and run

```bash
dotnet build src/Read2Me.App
dotnet run --project src/Read2Me.App
# https://localhost:5001 / http://localhost:5000
```

`Workspace.FolderPath` in `appsettings.json` (or `appsettings.Development.json`) sets the root data directory. All project folders, databases, audio files, and logs are written here. Leave empty to use the current directory.

### Two front ends, one host

The host serves the Blazor UI at `/` and, once it has been built, the Angular web app at `/app`. They
share the workspace, the API and the live hub, so a change made in one shows in the other.

```bash
pwsh scripts/build-web.ps1                 # npm ci + npm run build → src/Read2Me.App/wwwroot/app (git-ignored)
dotnet run --project src/Read2Me.App       # Blazor at http://localhost:5000/, web app at http://localhost:5000/app/
```

For web development with live reload run the host as above and, in a second terminal, `npm start` in
`src/Read2Me.Web` (Node 24 / npm 11): the dev server on <http://localhost:4200/app/> proxies the API and hub to
`:5000`. Until a bundle exists, `/app` answers with a page that says how to build one. Structure, conventions
and how to add a page: [docs/agents/web.md](docs/agents/web.md); the web project's own
[README](src/Read2Me.Web/README.md) lists every npm script.

## Infrastructure services

GPU-backed AI services live in `Infra/`. Run from that directory:

```bash
docker compose up -d llama              # LLM (character extraction / script classification)
docker compose up -d audiocpp          # TTS + voice design — every TTS model (audio.cpp)
docker compose up -d whisper           # CPU Whisper.CPP transcription (accuracy scoring)
docker compose up -d minilm-l6         # Semantic similarity (MiniLM-L6)
docker compose up -d mpnet-base-v2     # Semantic similarity (MPNet-Base-v2)
docker compose stop <service>
docker compose up -d --build           # After Dockerfile/entrypoint changes
docker logs -f <container>
```

Only one GPU-resident container at a time (8 GB VRAM limit). Whisper.CPP and the semantic similarity containers are CPU-only and can run alongside any GPU container.

See [Infra/README.md](Infra/README.md) for full service details, ports, and API reference.

## How it works

1. Import epub/text → parsed into Volume/Part/Chapter/Paragraph/ParagraphItem hierarchy
2. LLM (`read2me-llama`) attributes each dialog item to a Character
3. TTS service synthesises audio per ParagraphItem using the Character's voice + the item's optional voice instructions (followed by Breeze)
4. Whisper transcribes generated audio; WER + semantic similarity verify accuracy
5. Verified items assembled into `.m4b` with chapter markers, cover art, and metadata

## First-time setup (in the app)

After `dotnet run`, open <https://localhost:5001>. Configure the AI services from the left nav **before** processing a book — each settings page stores one or more named **configs** in `app.db`, with one marked **active**. Start the matching Docker container first (see [Infrastructure services](#infrastructure-services)).

| Nav page | Configure | Needs container |
| -------- | --------- | --------------- |
| **LLM** | LLM server URL + model preset for character attribution | `read2me-llama` |
| **LLM Prompts** | Prompt templates for extraction / attribution | — |
| **Transcription** | Whisper endpoint for accuracy scoring | `read2me-whisper` |
| **Semantic Similarity** | Embedding endpoint + pass threshold for Semantic Rescue | `read2me-minilm-l6` / `read2me-mpnet-base-v2` |
| **Voice Design** | Service for generating voices from a text description (Breeze / VoxCPM2 / Qwen3) | `read2me-audiocpp` |
| **Paragraph TTS** | TTS service(s) used to synthesise paragraph audio (Breeze / VoxCPM2 / Chatterbox / Qwen3 Base) | `read2me-audiocpp` |
| **Audio Processing** | WER threshold, retry attempts, pause durations, sentence chunking, post-processing (consonant softening, silence trim), **ffmpeg path** | — |

Set the **ffmpeg path** on the Audio Processing page — audio normalisation and m4b assembly fail without it.

## Using the app

1. **Create a project** — Home → add a project, then import an `.epub` or `.txt`. The book is parsed into the Volume → Part → Chapter → Paragraph → ParagraphItem hierarchy and shown on the project's **Book** tab.
2. **Attribute characters** — on the **Book** tab, switch the view mode to **Split: Attribution**. Select Character paragraphs (per node or whole chapters) and queue them. The Character Queue drains in the background, asking the LLM who speaks each line. Review/correct assignments on the **Characters** tab; add aliases there so alternate names resolve to one Character, and use **Discover characters** to build the roster up front.

   Every speaker is set from the same picker, on the item chip or the paragraph chip. The narrator is pinned at the top of that list, so a line the splitter misread as narration can be given to a character — and a narrative aside it mistook for dialog can be handed back to the narrator. Assigning to the narrator also stops the queue re-asking that item; clearing a speaker puts it back in the queue as unattributed dialog. Arm **Bulk assign** in the dock bar to apply one pick across a whole selection; narration is never swept up by it.
3. **Give each Character a voice** — on the **Characters** tab, add a voice per Character: upload a reference clip (up to 30 s; under 15 s clones best), or design one from a text description (the active Voice Design provider). Optionally add **Voice Rules** to switch voice over a position range. The batch buttons generate prompts/audio for all Characters at once. If a character in the book narrates it, link them to the narrator there — narration then reads in that character's voice while staying a distinct speaker from their dialog.
4. **Generate audio** — back on the **Book** tab, switch to **Split: Audio**. Select items needing audio and queue them. Each item is synthesised, loudness-normalised, transcribed by Whisper, and verified (WER, with Semantic Rescue as fallback). The status bar streams per-item progress; failures surface as review items on the node badges.
5. **Assemble the audiobook** — once every non-Pause item has audio, click **Assemble**. The app concatenates all clips (with per-kind pauses), adds chapter markers and cover art, and writes `{projectFolder}/output/{BookTitle}.m4b`.

## Driving it without the UI

The whole production cycle is also an HTTP API, served from the running app on `http://localhost:5000/api/...` — localhost only, no auth. Create a project, import, attribute, generate voices and audio, and assemble, all without opening a browser.

- **Workflow guide**: [docs/agents/api.md](docs/agents/api.md) — endpoints in the order you call them.
- **Schemas**: `GET /openapi/v1.json` is the source of truth for request/response shapes.

The four long operations (attribution, audio, voice batch, assembly) return `202 Accepted` and run in the background; poll their status endpoint until the queue is idle, then read per-item outcomes. Errors are RFC 7807 ProblemDetails.

## AI services and when to use them

| Service              | Container                  | Use for                                                                                                                             |
| -------------------- | -------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| **llama.cpp**        | `read2me-llama`            | Character extraction and dialog attribution. Run this during the script-processing stage.                                           |
| **audio.cpp**        | `read2me-audiocpp`         | Every TTS model, one loaded at a time: voice cloning for paragraph audio (Breeze TTS 2 — follows voice instructions — VoxCPM2, Chatterbox, Qwen3 Base) and voice design from a text description (Breeze, VoxCPM2, Qwen3 VoiceDesign). Breeze is research/non-commercial: personal use only. |
| **Whisper.CPP**      | `read2me-whisper`          | CPU-only transcription of generated audio for accuracy scoring (WER) and word-level alignment. Run alongside the active TTS container. |
| **MiniLM-L6**        | `read2me-minilm-l6`        | Semantic similarity check — rescues clips that fail WER but are semantically correct. CPU-only.                                     |
| **MPNet-Base-v2**    | `read2me-mpnet-base-v2`    | Same as MiniLM-L6 but a larger model with a different score scale. CPU-only.                                                        |

Typical session: start `llama` for attribution, then stop it and start `audiocpp` + `whisper` for audio generation.

## Databases

The app uses two SQLite databases, both managed automatically with EF Core migrations — no manual setup needed.

### `app.db` — application settings

Stored in the workspace root (`{Workspace.FolderPath}/app.db`). Shared across all projects. Holds:

- LLM server configs and prompt settings
- TTS service configs (Breeze, VoxCPM2, Chatterbox, Qwen3 Base)
- Voice design service configs
- Transcription service configs (Whisper)
- Semantic similarity service configs
- Audio processing settings (WER threshold, retry attempts, pause durations, sentence chunking)
- Text preprocessing steps (substitutions, sentence-case rules)
- App theme / UI settings

### `project.db` — per-project book data

One database per project, stored at `{Workspace.FolderPath}/{project-folder}/project.db`. Auto-created and migrated on first open. Holds:

- The book hierarchy: Volume → Part → Chapter → Paragraph → ParagraphItem
- Characters, aliases, voices, and voice rules
- Audio review outcomes (WER, Whisper transcript, normalisation result)

Audio files (WAV per ParagraphItem, reference voice WAVs) are stored in the same project folder alongside the database.

## Digging deeper

- [CONTEXT.md](CONTEXT.md) — the domain glossary: what a Character paragraph, a Generatable item or an unattributed item actually means, split by area under `context/`.
- [docs/adr/](docs/adr/) — the decisions behind the model, including why item boundaries are frozen after import ([ADR 0005](docs/adr/0005-frozen-paragraph-item-boundaries.md)) and why narration is a speaker rather than an item type ([ADR 0006](docs/adr/0006-narration-is-a-speaker-not-an-item-type.md)).
- [Infra/README.md](Infra/README.md) — service details, ports, and container API reference.

## Known issues

- More betterer error/warning. eg: Audio gen failed because no voice
