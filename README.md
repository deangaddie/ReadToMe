# ReadToMe

Converts text/epub book files into audiobooks via AI-powered character attribution and TTS synthesis.

## Requirements

- **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0>
- **Docker** (with NVIDIA GPU support) — for AI inference services
- **ffmpeg** — for audio normalisation and m4b assembly (path configured in app settings)
- **RTX 3070 or similar** — 8 GB VRAM minimum for GPU containers

## Build and run

```bash
dotnet build src/Read2Me.App
dotnet run --project src/Read2Me.App
# https://localhost:5001 / http://localhost:5000
```

`Workspace.FolderPath` in `appsettings.json` (or `appsettings.Development.json`) sets the root data directory. All project folders, databases, audio files, and logs are written here. Leave empty to use the current directory.

## Infrastructure services

GPU-backed AI services live in `Infra/`. Run from that directory:

```bash
docker compose up -d llama              # LLM (character extraction / script classification)
docker compose up -d chatterbox        # TTS — expression instructions + voice cloning
docker compose up -d chatterbox-turbo  # TTS — paralinguistic tags + voice cloning
docker compose up -d qwen3-tts         # TTS — voice design from text description
docker compose up -d qwen3-tts-base    # TTS — voice cloning from reference audio
docker compose up -d voxcpm2           # TTS — VoxCPM2 voice cloning
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
3. TTS service synthesises audio per ParagraphItem using the Character's voice + optional expression/paralinguistic hints
4. Whisper transcribes generated audio; WER + semantic similarity verify accuracy
5. Verified items assembled into `.m4b` with chapter markers, cover art, and metadata

## First-time setup (in the app)

After `dotnet run`, open <https://localhost:5001>. Configure the AI services from the left nav **before** processing a book — each settings page stores one or more named **configs** in `app.db`, with one marked **active**. Start the matching Docker container first (see [Infrastructure services](#infrastructure-services)).

| Nav page | Configure | Needs container |
| -------- | --------- | --------------- |
| **LLM Settings** | LLM server URL + model preset for character attribution | `read2me-llama` |
| **LLM Prompts** | Prompt templates for extraction / attribution | — |
| **Transcription Settings** | Whisper endpoint for accuracy scoring | `read2me-whisper` |
| **Semantic Similarity** | Embedding endpoint + pass threshold for Semantic Rescue | `read2me-minilm-l6` / `read2me-mpnet-base-v2` |
| **Voice Design Settings** | Service for generating voices from a text description | `read2me-qwen3-tts` |
| **Paragraph TTS Settings** | TTS service(s) used to synthesise paragraph audio | a TTS container (Chatterbox / VoxCPM2 / Qwen3 Base) |
| **Audio Processing** | WER threshold, retry attempts, pause durations, sentence chunking, **ffmpeg path** | — |

Set the **ffmpeg path** on the Audio Processing page — audio normalisation and m4b assembly fail without it.

## Using the app

1. **Create a project** — Home → add a project, then import an `.epub` or `.txt`. The book is parsed into the Volume → Part → Chapter → Paragraph → ParagraphItem hierarchy and shown on the project's **Book** tab.
2. **Attribute characters** — on the **Book** tab, switch the view-mode dropdown to **Split (attribution)**. Select Character paragraphs (per node or whole chapters) and queue them. The Character Queue drains in the background, asking the LLM who speaks each line. Review/correct assignments on the **Characters** tab; add aliases there so alternate names resolve to one Character.
3. **Give each Character a voice** — on the **Characters** tab, add a voice per Character: upload a reference WAV, design one from a text description (Qwen3 TTS), or clone from a reference clip. Optionally add **Voice Rules** to switch voice over a position range. The batch buttons generate prompts/audio for all Characters at once.
4. **Generate audio** — back on the **Book** tab, switch to **Split (audio)**. Select items needing audio and queue them. Each item is synthesised, loudness-normalised, transcribed by Whisper, and verified (WER, with Semantic Rescue as fallback). The status bar streams per-item progress; failures surface as review items on the node badges.
5. **Assemble the audiobook** — once every non-Pause item has audio, click **Assemble**. The app concatenates all clips (with per-kind pauses), adds chapter markers and cover art, and writes `{projectFolder}/output/{BookTitle}.m4b`.

## AI services and when to use them

| Service              | Container                  | Use for                                                                                                                             |
| -------------------- | -------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| **llama.cpp**        | `read2me-llama`            | Character extraction and dialog attribution. Run this during the script-processing stage.                                           |
| **Chatterbox**       | `read2me-chatterbox`       | TTS with expression instructions ("speak sadly") and fine-grained parameter control. Requires a reference voice WAV.                |
| **Chatterbox Turbo** | `read2me-chatterbox-turbo` | TTS with paralinguistic tags (`[laugh]`, `[sigh]`, `[gasp]`, etc.). Requires a reference voice WAV.                                 |
| **Qwen3 TTS**        | `read2me-qwen3-tts`        | TTS where you describe the voice in text ("a gruff old man"). No reference audio needed — good for generating a first voice sample. |
| **Qwen3 TTS Base**   | `read2me-qwen3-tts-base`   | TTS voice cloning from a reference audio clip and its transcript.                                                                   |
| **VoxCPM2**          | `read2me-voxcpm2`          | TTS voice cloning via VoxCPM2. Alternative to Chatterbox for cloning.                                                               |
| **Whisper.CPP**      | `read2me-whisper`          | CPU-only transcription of generated audio for accuracy scoring (WER) and word-level alignment. Run alongside the active TTS container. |
| **MiniLM-L6**        | `read2me-minilm-l6`        | Semantic similarity check — rescues clips that fail WER but are semantically correct. CPU-only.                                     |
| **MPNet-Base-v2**    | `read2me-mpnet-base-v2`    | Same as MiniLM-L6 but a larger model with a different score scale. CPU-only.                                                        |

Typical session: start `llama` for attribution, then stop it and start a TTS container + `whisper` for audio generation.

## Databases

The app uses two SQLite databases, both managed automatically with EF Core migrations — no manual setup needed.

### `app.db` — application settings

Stored in the workspace root (`{Workspace.FolderPath}/app.db`). Shared across all projects. Holds:

- LLM server configs and prompt settings
- TTS service configs (Chatterbox, VoxCPM2, Qwen3, etc.)
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

## Known issues

- More betterer error/warning. eg: Audio gen failed because no voice
