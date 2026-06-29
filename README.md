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
docker compose up -d whisper           # GPU transcription (accuracy scoring)
docker compose up -d whisper-cpu       # CPU transcription fallback
docker compose up -d minilm-l6         # Semantic similarity (MiniLM-L6)
docker compose up -d mpnet-base-v2     # Semantic similarity (MPNet-Base-v2)
docker compose stop <service>
docker compose up -d --build           # After Dockerfile/entrypoint changes
docker logs -f <container>
```

Only one GPU-resident container at a time (8 GB VRAM limit). The semantic similarity and whisper-cpu containers are CPU-only and can run alongside any GPU container.

See [Infra/README.md](Infra/README.md) for full service details, ports, and API reference.

## How it works

1. Import epub/text → parsed into Volume/Part/Chapter/Paragraph/ParagraphItem hierarchy
2. LLM (`read2me-llama`) attributes each dialog item to a Character
3. TTS service synthesises audio per ParagraphItem using the Character's voice + optional expression/paralinguistic hints
4. Whisper transcribes generated audio; WER + semantic similarity verify accuracy
5. Verified items assembled into `.m4b` with chapter markers, cover art, and metadata

## AI services and when to use them

| Service              | Container                  | Use for                                                                                                                             |
| -------------------- | -------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| **llama.cpp**        | `read2me-llama`            | Character extraction and dialog attribution. Run this during the script-processing stage.                                           |
| **Chatterbox**       | `read2me-chatterbox`       | TTS with expression instructions ("speak sadly") and fine-grained parameter control. Requires a reference voice WAV.                |
| **Chatterbox Turbo** | `read2me-chatterbox-turbo` | TTS with paralinguistic tags (`[laugh]`, `[sigh]`, `[gasp]`, etc.). Requires a reference voice WAV.                                 |
| **Qwen3 TTS**        | `read2me-qwen3-tts`        | TTS where you describe the voice in text ("a gruff old man"). No reference audio needed — good for generating a first voice sample. |
| **Qwen3 TTS Base**   | `read2me-qwen3-tts-base`   | TTS voice cloning from a reference audio clip and its transcript.                                                                   |
| **VoxCPM2**          | `read2me-voxcpm2`          | TTS voice cloning via VoxCPM2. Alternative to Chatterbox for cloning.                                                               |
| **Whisper**          | `read2me-whisper`          | Transcribes generated audio to score accuracy (WER). Run alongside the active TTS container.                                        |
| **Whisper CPU**      | `read2me-whisper-cpu`      | Same as Whisper but CPU-only. Use when VRAM is fully occupied.                                                                      |
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

- Loading a new book pulls epub cover but requires server restart to display it.
- As character assignment runs, it updates the statuses of paragraph items, but when it completes, all assignments are reset to "unknown" in the UI until reloading.
- Processing/thinking/response blocks in character assignment progress area should expand rather than sub-scroll.
- More betterer error/warning. eg: Audio gen failed because no voice
