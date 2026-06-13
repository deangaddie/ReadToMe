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
docker compose up -d read2me-chatterbox # TTS service
docker compose stop llama
docker compose up -d --build            # After Dockerfile/entrypoint changes
docker logs -f read2me-llama
```

## Architecture

**ReadToMe** is a Blazor Server app that orchestrates AI-powered audiobook production from text scripts.

### .NET App (`src/Read2Me.App`)

- **Framework**: ASP.NET Core 10, Blazor Server, `Startup.cs` pattern
- **Entry**: `Program.cs` → `Startup.cs` → `ConfigureServices` / `Configure`
- **UI**: Razor pages + Blazor components via SignalR

Currently bootstrapped (placeholder `Index.razor`). Domain services, repositories, and HTTP clients to the AI infra services will be registered in `Startup.ConfigureServices`.

### AI Infrastructure (`Infra/`)

Containerized GPU services orchestrated via `docker-compose.yml`. RTX 3070 (8 GB VRAM) — **only one GPU-resident container at a time**.

| Container | Port | Role |
|-----------|------|------|
| `read2me-llama` | 8080 | LLM — character extraction, script classification. OpenAI-compatible API. |
| `read2me-chatterbox` | 8000 | TTS — expression instructions + voice cloning (`POST /tts`) |
| `read2me-chatterbox-turbo` | 8001 | TTS — paralinguistic tags (`[laugh]`, `[sigh]`, etc.) + voice cloning (`POST /tts/turbo`) |
| `read2me-qwen3-tts` | 8100 | TTS — voice design from text description, no reference audio (`POST /tts`) |
| `read2me-qwen3-tts-base` | 8101 | TTS — voice cloning from reference audio + transcript (`POST /tts`) |
| `read2me-whisper` | 9000 | Audio transcription for accuracy scoring — small enough to co-run with Chatterbox |

**Chatterbox** requires `reference_audio` (WAV/MP3) on every request — no built-in voices.

**llama.cpp** uses a TurboQuant KV-cache fork. Switch model without restart:
```bash
curl -X POST http://localhost:8080/v1/models -d '{"model":"gemma-26b"}'
```
Model presets: `gemma-26b`, `qwen-36b`, `gemma-4b`, `qwen-9b` — defined in `Infra/llama/config/models.ini`.

GGUF model files live in `Infra/models/` (bind-mounted, not committed).

### Intended Data Flow

User script → LLM (`read2me-llama`) for character/script processing → TTS service for audio generation → Whisper for accuracy scoring → assembled audiobook output.
