# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Solution

`src/Read2Me.slnx` — .NET 10 solution (slnx format, not sln).

## Commands

```bash
# Build
dotnet build src/Read2Me.App

# Run (Kestrel)
dotnet run --project src/Read2Me.App
# https://localhost:5001 / http://localhost:5000

# Infrastructure services (run from Infra/)
docker compose up -d llama              # LLM service
docker compose up -d chatterbox        # TTS — expression + voice cloning
docker compose up -d chatterbox-turbo  # TTS — paralinguistic tags
docker compose up -d qwen3-tts         # TTS — voice design from description
docker compose up -d qwen3-tts-base    # TTS — voice cloning from reference audio
docker compose up -d voxcpm2           # TTS — VoxCPM2 voice cloning
docker compose up -d whisper           # CPU Whisper.CPP transcription
docker compose up -d minilm-l6         # Semantic similarity
docker compose up -d mpnet-base-v2     # Semantic similarity
docker compose stop <service>
docker compose up -d --build            # After Dockerfile/entrypoint changes
docker logs -f read2me-llama
```

## Architecture

**ReadToMe** is a Blazor Server app that orchestrates AI-powered audiobook production from text scripts.

### .NET App (`src/Read2Me.App`)

- **Framework**: ASP.NET Core 10, Blazor Server, `Startup.cs` pattern
- **Entry**: `Program.cs` → `Startup.cs` → `ConfigureServices` / `Configure`
- **UI**: Razor pages + Blazor components via SignalR

### AI Infrastructure (`Infra/`)

Containerized GPU services orchestrated via `docker-compose.yml`. RTX 3070 (8 GB VRAM) — only one GPU-resident container at a time.

| Container | Port | Role |
|-----------|------|------|
| `read2me-llama` | 8080 | LLM — character extraction, script classification. OpenAI-compatible API. |
| `read2me-chatterbox` | 8000 | TTS — expression instructions + voice cloning (`POST /tts`) |
| `read2me-chatterbox-turbo` | 8001 | TTS — paralinguistic tags (`[laugh]`, `[sigh]`, etc.) + voice cloning (`POST /tts/turbo`) |
| `read2me-qwen3-tts` | 8100 | TTS — voice design from text description, no reference audio (`POST /tts`) |
| `read2me-qwen3-tts-base` | 8101 | TTS — voice cloning from reference audio + transcript (`POST /tts`) |
| `read2me-voxcpm2` | 8003 | TTS — VoxCPM2 voice cloning (`POST /tts`) |
| `read2me-whisper` | 9000 | CPU-only Whisper.CPP transcription for accuracy scoring |
| `read2me-minilm-l6` | 8200 | Semantic similarity — MiniLM-L6 (`POST /similarity`) |
| `read2me-mpnet-base-v2` | 8201 | Semantic similarity — MPNet-Base-v2 (`POST /similarity`) |

**Chatterbox** requires `reference_audio` (WAV/MP3) on every request — no built-in voices.

**llama.cpp** uses a TurboQuant KV-cache fork. Switch model without restart via autoload — name the target model in an inference request; `--models-max 1` evicts the loaded model (`POST /v1/models` does NOT switch on this fork build — it 404s):
```bash
curl http://localhost:8080/v1/chat/completions -d '{"model":"gemma-26b","messages":[{"role":"user","content":"hi"}],"max_tokens":1}'
```
Probe the loaded preset with `GET /v1/models` (each item's `status.value` is `unloaded`/`loading`/`loaded`).
Model presets defined in `Infra/llama/config/models.ini` (e.g. `gemma-26b`, `qwen-28b`, `gemma-12b_QAT`, `gemma-4b`, `qwen-9b`, `qwen-4b`, `ornith-1.0-9b-q4`).

GGUF model files live in `Infra/models/` (bind-mounted, not committed).

### Data Flow

User imports epub/text → LLM attributes dialog items to Characters → TTS synthesises audio per ParagraphItem → Whisper transcribes for WER/semantic accuracy check → items assembled into `.m4b` audiobook with chapter markers and cover art.

## Agent skills

### Issue tracker

Issues and specs (PRDs) live as local markdown under `.scratch/<feature-slug>/`. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: `CONTEXT.md` + `docs/adr/` at repo root. See `docs/agents/domain.md`.
