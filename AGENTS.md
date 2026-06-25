# AGENTS for ReadToMe

This repository is a Blazor Server app plus GPU-backed AI inference infrastructure for audiobook production.

## Use when

- working on code in `src/Read2Me.App`
- updating or extending the Blazor Server app
- changing AI service orchestration or Docker-based inference dependencies
- fixing build/run issues for the .NET app or the `Infra/` services

## Key facts

- App: `src/Read2Me.App` is an ASP.NET Core 10 Blazor Server app using the `Program.cs` + `Startup.cs` pattern.
- UI: Razor pages and Blazor components under `Pages/` and `Shared/`.
- Solution: `src/Read2Me.slnx`.
- Infra: `Infra/` contains GPU-backed AI service containers for LLM, TTS, Whisper, and semantic similarity.
- Models: `Infra/models/` holds GGUF model files; these are not committed and must be provided separately.

## Build and run

- Build: `dotnet build src/Read2Me.App`
- Run: `dotnet run --project src/Read2Me.App`
- App host: <https://localhost:5001> and <http://localhost:5000> by default.

## Infrastructure services

Use `docker compose` from the `Infra/` directory.

```bash
docker compose up -d llama
docker compose up -d chatterbox
docker compose up -d chatterbox-turbo
docker compose up -d qwen3-tts
docker compose up -d qwen3-tts-base
docker compose up -d voxcpm2
docker compose up -d whisper
docker compose up -d whisper-cpu
docker compose up -d minilm-l6
docker compose up -d mpnet-base-v2
docker compose up -d --build   # after Dockerfile or entrypoint changes
docker compose down            # stop everything
docker logs -f <container>     # follow logs
```

## Important constraints

- GPU setup is VRAM-limited (RTX 3070, 8 GB). Only one GPU-resident container should run at a time in normal use.
- `read2me-whisper` can usually run alongside a Chatterbox container; other GPU containers are typically exclusive.
- `read2me-minilm-l6` and `read2me-mpnet-base-v2` are CPU-only and can run alongside any GPU container.
- `read2me-chatterbox` and `read2me-qwen3-tts-base` require `reference_audio` for voice cloning; there are no built-in voices.
- `read2me-voxcpm2` also requires `reference_audio`.
- `read2me-qwen3-tts` generates voices from a text description; no reference audio needed.

## Relevant files

- `CLAUDE.md` — repository overview, build/run commands, and architecture summary
- `CONTEXT.md` — domain glossary; read before any architecture work
- `Infra/README.md` — Docker service details, ports, supported endpoints, and usage notes
- `Infra/docker-compose.yml` — container orchestration for all services
- `src/Read2Me.App/Program.cs` and `Startup.cs` — application startup and middleware configuration
- `src/Read2Me.Data/datamodel.md` — entity schema reference

## Agent guidance

- Prefer linking to `CLAUDE.md` or `Infra/README.md` for detailed infra behaviour rather than duplicating those docs.
- When modifying infrastructure or AI service integration, verify service startup commands and port mappings in `Infra/docker-compose.yml`.
- If asked to add features, confirm whether the work is on the UI app, the AI service layer, or the Docker infra.
