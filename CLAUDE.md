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
# https://localhost:5001 / http://localhost:5000 — `/` redirects to the Angular app at /app (default UI, ADR 0009); legacy Blazor home at /blazor

# Infrastructure services (run from Infra/)
docker compose up -d llama              # LLM service
docker compose up -d audiocpp          # TTS + voice design — every TTS model (audio.cpp, ADR 0010)
docker compose up -d whisper           # CPU Whisper.CPP transcription
docker compose up -d minilm-l6         # Semantic similarity
docker compose up -d mpnet-base-v2     # Semantic similarity
docker compose stop <service>
docker compose up -d --build            # After Dockerfile/entrypoint changes
docker logs -f read2me-llama

# Web front end (Angular, src/Read2Me.Web — see its README.md and docs/agents/web.md)
pwsh scripts/build-web.ps1              # npm ci + npm run build from anywhere; -Check runs npm run check instead
cd src/Read2Me.Web
npm ci                                  # Node 24 / npm 11 pinned in engines
npm start                               # ng serve on http://localhost:4200/app/, proxies /api,/hubs,/workspace,/openapi to :5000
npm run build                           # emits to src/Read2Me.App/wwwroot/app/ (git-ignored); host serves it at /app
npm run check                           # lint + typecheck + api:check + test + build
npm run api:types                       # regenerate src/app/api/schema.d.ts from a running host /openapi/v1.json

# Browser tests (both UIs): src/Read2Me.E2eTests — xUnit + Playwright over an in-proc host with fake AI.
dotnet test src/Read2Me.E2eTests         # Angular tests (Tests/Web) skip unless a bundle exists in wwwroot/app
dotnet test src/Read2Me.E2eTests --filter "FullyQualifiedName~Tests.Web"   # only the Angular suite
# Ad-hoc browser driving of a running host: tools/browse/README.md (or the `verify` skill)
```

## Architecture

**ReadToMe** orchestrates AI-powered audiobook production from text scripts. The default UI is the Angular app (`src/Read2Me.Web`), served by the host at `/app` as a thin client over the agent API and the live hub — see `docs/agents/web.md` and ADRs 0008/0009. The legacy Blazor Server UI (home at `/blazor`) remains until it is removed; add new UI work to the Angular app only.

### .NET App (`src/Read2Me.App`)

- **Framework**: ASP.NET Core 10, Blazor Server, `Startup.cs` pattern
- **Entry**: `Program.cs` → `Startup.cs` → `ConfigureServices` / `Configure`
- **UI**: Razor pages + Blazor components via SignalR

### AI Infrastructure (`Infra/`)

Containerized GPU services orchestrated via `docker-compose.yml`. RTX 3070 (8 GB VRAM) — only one GPU-resident container at a time.

| Container | Port | Role |
|-----------|------|------|
| `read2me-llama` | 8080 | LLM — character extraction, script classification. OpenAI-compatible API. |
| `read2me-audiocpp` | 8004 | TTS + voice design — audio.cpp runtime serving every TTS model (`POST /v1/audio/speech`) |
| `read2me-whisper` | 9000 | CPU-only Whisper.CPP transcription for accuracy scoring |
| `read2me-minilm-l6` | 8200 | Semantic similarity — MiniLM-L6 (`POST /similarity`) |
| `read2me-mpnet-base-v2` | 8201 | Semantic similarity — MPNet-Base-v2 (`POST /similarity`) |

**audio.cpp** (ADR 0010) serves the model ids in `Infra/audiocpp/server.json` — `breeze-q8`, `breeze-design`, `voxcpm2`, `chatterbox`, `qwen3-base`, `qwen3-design`; each provider config's `ModelId` names one. It keeps one model resident and switches on request, like llama autoload: the first request after a switch takes 9–31 s, and a request for a *different* model mid-generation gets a 503, so the app sends one request per endpoint at a time (the TTS gate). `server.json` is read at container start — restart `audiocpp` after editing it. GGUFs live in `GGUF_MODELS_DIR` under `Breeze-TTS-2-GGUF/`, `VoxCPM2-GGUF/`, `Chatterbox-GGUF/` and `Qwen3-TTS-12Hz-1.7B-{Base,VoiceDesign}-GGUF/`. Breeze weights and its audio output are BreezeBlue research/non-commercial: personal use only. Details in `Infra/README.md`.

**llama.cpp** is upstream `v0.5.0` (a TurboQuant fork until 2026-09-26; no `turbo*` KV types now). Switch model without restart via autoload — name the target model in an inference request; `--models-max 1` evicts the loaded model (`POST /v1/models` 404ed on the old fork; the app uses autoload):
```bash
curl http://localhost:8080/v1/chat/completions -d '{"model":"gemma-26b","messages":[{"role":"user","content":"hi"}],"max_tokens":1}'
```
Probe the loaded preset with `GET /v1/models` (each item's `status.value` is `unloaded`/`loading`/`loaded`).
Model presets defined in `Infra/llama/config/models.ini` (e.g. `gemma-26b`, `qwen-28b`, `gemma-12b_QAT`, `gemma-4b`, `qwen-9b`, `qwen-4b`, `ornith-1.0-9b-q4`).

GGUF model files live in `Infra/models/` by default (bind-mounted, not committed). Override the host directory with `GGUF_MODELS_DIR` in `Infra/.env` to share GGUFs across projects; container path stays `/models`. Whisper's `ggml-base.en.bin` stays in `Infra/models/`.

### Data Flow

User imports epub/text → LLM attributes dialog items to Characters → TTS synthesises audio per ParagraphItem → Whisper transcribes for WER/semantic accuracy check → items assembled into `.m4b` audiobook with chapter markers and cover art.

## Agent skills

### Issue tracker

Issues and specs (PRDs) live as local markdown under `.scratch/<feature-slug>/`. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: `CONTEXT.md` + `docs/adr/` at repo root. See `docs/agents/domain.md`.
