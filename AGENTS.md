# AGENTS for ReadToMe

This repository is an ASP.NET Core API host with two web front ends (see `docs/agents/web.md`): the Angular app at `/app`, which is **frozen (bug fixes only)**, and the native app at `/app2` (custom elements on Bun, ADR 0014) that is replacing it screen by screen — new UI work goes there. Plus AI inference infrastructure for audiobook production, with GPU-backed generation services and CPU-only transcription/semantic services. The migration plan is `.scratch/native-web/spec.md` (local, untracked).

## Use when

- working on code in `src/Read2Me.App`
- updating or extending the API host, the native app, or (bug fixes only) the Angular app
- changing AI service orchestration or Docker-based inference dependencies
- fixing build/run issues for the .NET app or the `Infra/` services

## Key facts

- App: `src/Read2Me.App` is an ASP.NET Core 10 host using the `Program.cs` + `Startup.cs` pattern: agent API under `Api/`, the live hub under `Live/`.
- UI: the Angular app in `src/Read2Me.Web`, built into `src/Read2Me.App/wwwroot/app` and served at `/app` (frozen); the native app in `src/Read2Me.Native`, built into `src/Read2Me.App/wwwroot/app2` and served at `/app2` (where new screens land). Neither is part of the .NET solution.
- Solution: `src/Read2Me.slnx`.
- Infra: `Infra/` contains GPU-backed LLM/TTS containers plus CPU-only Whisper and semantic similarity containers.
- Models: `Infra/models/` holds GGUF model files by default; these are not committed and must be provided separately. `GGUF_MODELS_DIR` in `Infra/.env` can point the llama mount at a shared directory outside the repo.

## Build and run

- Build: `dotnet build src/Read2Me.App`
- Run: `dotnet run --project src/Read2Me.App`
- App host: <https://localhost:5001> and <http://localhost:5000> by default.
- Angular app (frozen): `pwsh scripts/build-web.ps1` (or `npm ci` + `npm run build` in `src/Read2Me.Web`); dev server `npm start` on `:4200/app/`; gate `npm run check`.
- Native app: `pwsh scripts/build-native.ps1` (or `bun install --frozen-lockfile` + `bun run build` in `src/Read2Me.Native`); dev server `bun run dev` on `:4300/app2/`; gate `bun run check`; one spec `bun test <file>`.
- Browser tests: `dotnet test src/Read2Me.E2eTests` — `Tests/Web` needs the `/app` bundle, `Tests/Native` the `/app2` bundle (a missing bundle skips its suite); `R2M_E2E_BROWSER=firefox` runs the native classes in Firefox.
- PR gate, every CI stage in order: `pwsh scripts/check.ps1` (`-SkipInstall` once dependencies are current).
- Ad-hoc driving of a running host: `tools/browse` (`R2M_APP=/app2` for the native app) or the `verify` skill.

## Infrastructure services

Use `docker compose` from the `Infra/` directory.

```bash
docker compose up -d llama
docker compose up -d audiocpp        # every TTS + voice-design model (audio.cpp, ADR 0010)
docker compose up -d whisper
docker compose up -d minilm-l6
docker compose up -d mpnet-base-v2
docker compose up -d --build   # after Dockerfile or entrypoint changes
docker compose down            # stop everything
docker logs -f <container>     # follow logs
```

## Important constraints

- GPU setup is VRAM-limited (RTX 3070, 8 GB). Only one GPU-resident container should run at a time in normal use.
- Host has 32 GB RAM; llama mlocks its weights inside the WSL2 VM, so `.wslconfig` must set `memory=16GB` (see `Infra/README.md`, "Host / WSL memory"). The app's LLM preset is `qwen-28b` ("Attribution preset" there), plus `gemma-12b` for an opt-in Chapter-style attribution config (ADR 0013).
- `read2me-whisper`, `read2me-minilm-l6` and `read2me-mpnet-base-v2` are CPU-only and can run alongside any GPU container.
- `read2me-audiocpp` serves every TTS model (ids in `Infra/audiocpp/server.json`), one resident at a time, switching on request. Paragraph TTS clones from a voice's reference audio; voice design (`breeze-design`, `qwen3-design`, `voxcpm2`) works from a text description.

## Relevant files

- `CLAUDE.md` — repository overview, build/run commands, and architecture summary
- `docs/agents/web.md` — both web front ends: the frozen Angular app (`src/Read2Me.Web`, `/app`: layout, lint rules, adding a page, the live hub contract, browser tests) and the native app (`src/Read2Me.Native`, `/app2`: commands, layout, conventions, primitive catalogue, `WebApp` for E2E, gotchas); vocabulary in `context/web.md`
- `docs/adr/0014-native-web-front-end-replaces-angular.md` — why the front end is moving off Angular and what the native app is built on
- `CONTEXT.md` — domain glossary index; read before any architecture work, then load only the `context/*.md` section file(s) for the area you're touching
- `Infra/README.md` — Docker service details, ports, supported endpoints, and usage notes
- `Infra/docker-compose.yml` — container orchestration for all services
- `src/Read2Me.App/Program.cs` and `Startup.cs` — application startup and middleware configuration
- `src/Read2Me.Data/datamodel.md` — entity schema reference

## Agent guidance

- Prefer linking to `CLAUDE.md` or `Infra/README.md` for detailed infra behaviour rather than duplicating those docs.
- When modifying infrastructure or AI service integration, verify service startup commands and port mappings in `Infra/docker-compose.yml`.
- If asked to add features, confirm whether the work is on the UI app, the AI service layer, or the Docker infra.
