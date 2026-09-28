# Infra

Docker infrastructure for ReadToMe AI services.

The standalone local service console is documented in
[ContainerHealth/README.md](ContainerHealth/README.md). It links the locked setup/start,
target override, deterministic verification, and live acceptance workflows without
duplicating the service contracts below.

## Structure

```text
Infra/
├── docker-compose.yml          # All AI service containers
├── Dockerfile.llama            # llama.cpp server (upstream v0.5.0, multi-model preset)
├── Dockerfile.whisper          # Whisper.CPP server (CPU)
├── Dockerfile.minilm-l6        # Semantic similarity (MiniLM-L6)
├── Dockerfile.mpnet-base-v2    # Semantic similarity (MPNet-Base-v2)
├── llama/
│   ├── entrypoint.sh           # Starts llama-server with model presets
│   └── config/
│       └── models.ini          # Model preset definitions
├── audiocpp/
│   └── server.json             # audio.cpp model entries (every TTS model)
└── models/                     # GGUF model files (bind-mounted, not committed)
```

## Services

| Service              | Container                   | Port | Purpose                                                          |
| -------------------- | --------------------------- | ---- | ---------------------------------------------------------------- |
| llama.cpp            | `read2me-llama`             | 8080 | LLM — character extraction, script classification                |
| audio.cpp            | `read2me-audiocpp`          | 8004 | TTS + voice design — every TTS model, one loaded at a time (ADR 0010) |
| Whisper.CPP          | `read2me-whisper`           | 9000 | CPU-only transcription for WER and word-level alignment          |
| MiniLM-L6            | `read2me-minilm-l6`         | 8200 | Semantic similarity — MiniLM-L6-v2                               |
| MPNet-Base-v2        | `read2me-mpnet-base-v2`     | 8201 | Semantic similarity — all-mpnet-base-v2                          |

### Health checks

Every service defines its Docker health check in its image, except audio.cpp,
which runs the upstream image unchanged and defines it in `docker-compose.yml`. Docker runs the
container URL from inside the container; use the host URL for a manual check
from the machine running Compose. All published ports are bound to
`127.0.0.1`, and each probe succeeds on an HTTP `200` response.

| Service | Docker probe inside container | Manual probe from host |
| --- | --- | --- |
| llama.cpp | `GET http://localhost:8080/health` | `http://127.0.0.1:8080/health` |
| audio.cpp | `GET http://localhost:8080/health` | `http://127.0.0.1:8004/health` |
| Whisper.CPP | `GET http://127.0.0.1:8080/health` | `http://127.0.0.1:9000/health` |
| MiniLM-L6 | `GET http://localhost:8200/docs` | `http://127.0.0.1:8200/docs` |
| MPNet-Base-v2 | `GET http://localhost:8201/docs` | `http://127.0.0.1:8201/docs` |

The semantic services use FastAPI's generated Swagger UI at `/docs` as their
Docker liveness probe; they do not expose a separate `/health` route. Because
their models load before the server starts accepting requests, a successful
probe also confirms model initialization. Check Docker's current view with
`docker compose ps`.

## GPU / VRAM note

Configured for RTX 3070 (8 GB VRAM). GPU-resident services cannot generally run together at this VRAM budget. CPU-only Whisper and the semantic-similarity services can run alongside a GPU service.

| Container                   | When to run                                      |
| --------------------------- | ------------------------------------------------ |
| `read2me-llama`             | LLM tasks (script processing)                    |
| `read2me-audiocpp`          | Paragraph TTS and voice design (all TTS models)  |
| `read2me-whisper`           | CPU transcription for WER and word-level alignment |
| `read2me-minilm-l6`         | Semantic similarity (no GPU — CPU only)          |
| `read2me-mpnet-base-v2`     | Semantic similarity (no GPU — CPU only)          |

> **Note:** Whisper.CPP and the semantic similarity containers are CPU-only and can run alongside `read2me-audiocpp`.

## Usage

```bash
# Start all services (only do this if VRAM budget allows)
docker compose up -d

# Start a single service
docker compose up -d llama

# Stop a single service
docker compose stop llama

# Rebuild (e.g. after changing a Dockerfile or entrypoint)
docker compose up -d --build

# Stop all
docker compose down

# Tail logs
docker compose logs -f

# Tail a specific service
docker logs -f read2me-llama
```

## Model cache warm-up

A cold `Infra/cache/` is an unbootable stack by design. Populate or refresh a
model cache with the standalone warm-up compose file before starting its
hardened service:

```bash
docker compose -f docker-compose.warmup.yml run --rm <service>
```

For example, `docker compose -f docker-compose.warmup.yml run --rm minilm-l6`
downloads the pinned MiniLM snapshot into `cache/minilm-l6`. Likewise,
`docker compose -f docker-compose.warmup.yml run --rm whisper` provisions the
pinned Whisper artifact in `models/`, verifying its source revision, SHA-256,
and byte length before an atomic replacement. The warm-up file is the
executable model-pin table: changing a model revision is deliberately a
two-step operation — update its warm-up service, run it, then start the normal
service. Do not merge this file with `docker-compose.yml`; the hardened stack's
DNS policy must be absent while a model is being downloaded.

## llama.cpp

Custom image built from `Dockerfile.llama` using upstream `ggml-org/llama.cpp` pinned at `v0.5.0` (commit `7fe450e19305b828c199d602c23a8337aaa1f03b`). It replaced the TurboQuant KV-cache fork (`4503343`) after an A/B found the same speed, a VRAM fit, and no breakage (`.scratch/llm-model-upgrade/research/14-bump-ab/STATUS.md`). Serves an OpenAI-compatible API (`/v1/chat/completions`, `/v1/models`).

The pin is frozen until there is a reason to move it: there is no update cadence or Dependabot entry. Before any bump, review the upstream changes between the two SHAs. Any change to networking, file I/O outside the model path, or build scripts blocks the bump, and a bump needs a replay A/B like ticket 14's.

Model presets are defined in `llama/config/models.ini`. Multiple models can be configured; only one is loaded at a time (`--models-max 1`). Switch without restart via **autoload**: name the target model in an inference request and the server evicts the currently loaded model to make room. (The request blocks until the new model finishes loading, then responds.)

```bash
# Autoload gemma-26b by naming it in a chat-completion request:
curl http://localhost:8080/v1/chat/completions \
  -d '{"model":"gemma-26b","messages":[{"role":"user","content":"hi"}],"max_tokens":1}'
```

> **Note:** `POST /v1/models` did **not** switch models on the old fork build (it 404ed) and is untested on upstream. Autoload (above) is the switch the app uses.

Probe which preset is currently loaded with `GET /v1/models` — each preset item carries a `status.value` of `unloaded`, `loading`, or `loaded`:

```bash
curl http://localhost:8080/v1/models
```

All presets are configured for a 34000-token context (`c = 34000`).

| Preset | Model file |
| --- | --- |
| `gemma-26b` | `gemma-4-26B-A4B-it-UD-Q4_K_M.gguf` |
| `gemma-26b_QAT` | `gemma-4-26B-A4B-it-qat-UD-Q4_K_XL.gguf` |
| `gemma-12b_QAT` | `gemma-4-12B-it-qat-UD-Q4_K_XL.gguf` |
| `gemma-4b` | `gemma-4-E4B-it-UD-Q4_K_XL.gguf` |
| `qwen-28b` | `Qwen3.6-28B-REAP20-A3B-Q4_K_M.gguf` |
| `qwen-9b` | `Qwen3.5-9B-UD-Q4_K_XL.gguf` |
| `qwen-4b` | `Qwen3.5-4B-UD-Q4_K_XL.gguf` |
| `ornith-1.0-9b-q4` | `ornith-1.0-9b-Q4_K_M.gguf` |
| `ornith-1.0-9b-q5` | `ornith-1.0-9b-Q5_K_M.gguf` |

GGUF files must be placed in `models/` before building. Example:

```bash
pip install huggingface-hub
huggingface-cli download <repo> --local-dir ./models
```

- `IPC_LOCK` capability + unlimited `memlock` to keep model in RAM
- GPU layers offloaded via `ngl = 999` in preset config
- Logs bind-mounted to `./logs`

Port `8080`.

## audio.cpp (TTS)

`read2me-audiocpp` runs [audio.cpp](https://github.com/0xShug0/audio.cpp), a C++/ggml runtime, and serves every paragraph-TTS and voice-design model the app uses (ADR 0010). Port `8004` on the host, `8080` in the container.

The upstream image `ghcr.io/0xshug0/audio.cpp:full-cuda12` runs unchanged — no Dockerfile of our own — pinned by its amd64 digest on the `image:` line in `docker-compose.yml`. `scripts/refresh-pins.ps1` covers only Dockerfile bases, so bump this pin by hand: resolve the tag (`docker buildx imagetools inspect ghcr.io/0xshug0/audio.cpp:full-cuda12`), rewrite the digest, and check each model still generates.

### Models — `audiocpp/server.json`

| Model id | Family | Task | Used by | GGUF under `GGUF_MODELS_DIR` |
| --- | --- | --- | --- | --- |
| `breeze-q8` | `breeze_tts` | `clon` | Breeze paragraph TTS | `Breeze-TTS-2-GGUF/breeze-tts-2-q8_0.gguf` |
| `breeze-design` | `breeze_tts` | `tts` | Breeze voice design | same file as `breeze-q8` |
| `voxcpm2` | `voxcpm2` | `tts` | VoxCPM2 paragraph TTS and voice design | `VoxCPM2-GGUF/voxcpm2-q8_0.gguf` |
| `chatterbox` | `chatterbox` | `clon` | Chatterbox paragraph TTS | `Chatterbox-GGUF/chatterbox-q8_0.gguf` |
| `qwen3-base` | `qwen3_tts` | `tts` | Qwen3 Base paragraph TTS | `Qwen3-TTS-12Hz-1.7B-Base-GGUF/qwen3-tts-12hz-1.7b-base-q8_0_v2.gguf` |
| `qwen3-design` | `qwen3_tts` | `vdes` | Qwen3 voice design | `Qwen3-TTS-12Hz-1.7B-VoiceDesign-GGUF/qwen3-tts-12hz-1.7b-voicedesign-q8_0.gguf` |

Each provider config's `ModelId` names its entry; the ids above are the defaults. The Q8_0 GGUFs come from the Hugging Face repo `audio-cpp/audio.cpp-gguf`, one folder per model — keep the folder when downloading:

```bash
huggingface-cli download audio-cpp/audio.cpp-gguf --include "Breeze-TTS-2-GGUF/*q8_0.gguf" --local-dir <GGUF_MODELS_DIR>
```

- **Read at startup.** An added or edited entry goes live only after `docker compose restart audiocpp`.
- **Lazy load, one resident** (`lazy_load: true`, `max_loaded_models: 1`, no idle unload). A request naming another model loads it and evicts the current one; that first request takes 9–31 s. Probe with `GET /v1/models`: each `data[]` item carries a `loaded` boolean (not llama's `status.value`).
- **A missing GGUF does not fail startup.** `/v1/models` still lists the entry with `loaded: false`, and a request for it returns 500 `model path does not exist`.
- **Busy.** A request for a *different* model while one is generating gets an immediate 503 `server_busy`; requests for the same model queue. The app's TTS gate sends one request per endpoint at a time and retries a 503 after 2, 4 and 8 s before reporting the queue item Busy.
- **VoxCPM2 reference capacity.** `voxcpm2.audiovae_encoder_sample_capacity: "480000"` raises VoxCPM2's reference-audio cap from 15 s to 30 s. It pairs with the app's 30 s hard Reference Limit (`ReferenceLimit` in `Read2Me.Services.Audio`); raise one and the other must follow. audio.cpp also caps an inline reference at 5 MiB, which is the limit's byte bound.

### Request

`POST /v1/audio/speech`, JSON `{ model, input, voice_ref?: { type: "base64", data }, reference_text?, language?, options: { … } }`. Every `options` value is a **string**. The response is a WAV at the model's native rate; the app normalises it to Canonical WAV.

- **Breeze** clone sends the voice transcript as `reference_text` and the item's voice instructions as `options.instruction` (omitted when empty), with `guidance_scale` 3 when instructed and 1 when not. Breeze design sends the design prompt as `options.instruction` and no reference.
- **VoxCPM2** takes the instruction in `input` as `(instruction)text` and no `reference_text`.
- **Qwen3** takes `language` top-level as a name (`english`, `auto`, …); audio.cpp ignores `options.language`. Base sends the voice transcript as `reference_text`.
- **Chatterbox** sends `language: "en"` and every knob explicitly, because audio.cpp's documented defaults differ from its code.
- Every request carries a random `seed` unless the config pins one — audio.cpp's default is a fixed 0, which would make a WER retry repeat the same take.

### VRAM

VoxCPM2 and Qwen3 peak near 7.75 GB on paragraph-sized requests — the 8 GB card has no room for llama beside them, and requests should stay paragraph-sized.

### Licences

The audio.cpp runtime is Apache-2.0; VoxCPM2 and Qwen3 are Apache-2.0 and Chatterbox is MIT. **Breeze TTS 2 weights are BreezeBlue research/non-commercial, and the licence covers the audio they produce: personal use only.** Cloning a real person's voice needs their consent.

### Removing the native TTS containers

Before ADR 0010 each model ran in its own Python container (`chatterbox`, `chatterbox-turbo`, `qwen3-tts`, `qwen3-tts-base`, `voxcpm2`). The app migrates stored configs itself (localhost configs on 8000/8003/8100/8101 move to 8004; Chatterbox Turbo configs are deleted). On a machine that ran the old containers, remove the leftovers by hand from `Infra/`:

```bash
docker rm -f read2me-chatterbox read2me-chatterbox-turbo read2me-qwen3-tts read2me-qwen3-tts-base read2me-voxcpm2
docker image rm read2me-chatterbox read2me-chatterbox-turbo read2me-qwen3-tts read2me-qwen3-tts-base read2me-voxcpm2
docker volume rm infra_voxcpm2_uploads
```

Then delete `cache/chatterbox`, `cache/chatterbox-turbo`, `cache/qwen3`, `cache/qwen3-base` and `cache/voxcpm2`.

## Whisper.CPP (CPU)

`read2me-whisper` is a CPU-only, hardened Whisper.CPP `v1.8.5` server on port
9000. Before its first start, provision the exact pinned `base.en` artifact
through the shared model warm-up flow:

```powershell
docker compose -f docker-compose.warmup.yml run --rm whisper
docker compose up -d whisper
```

The warm-up verifies the model's immutable source revision, SHA-256 and byte
length before atomically placing `models/ggml-base.en.bin`. The service mounts
that one file read-only, runs as uid/gid 10001, has a read-only root filesystem
with writable `/tmp`, and deliberately has no model cache, runtime download
path, GPU reservation, or outbound DNS route. It becomes healthy only after the
model loads; use its upstream `POST /inference` endpoint with the Read2Me
Canonical WAV protocol. Its verbose-JSON response includes the word-level
timings used wherever precise text-to-audio alignment is required.

## Semantic Similarity — MiniLM-L6 and MPNet-Base-v2

Two CPU-only sentence-transformer containers for semantic verification of TTS output. Both expose the same API.

| Container               | Port | Model               |
| ----------------------- | ---- | ------------------- |
| `read2me-minilm-l6`     | 8200 | `all-MiniLM-L6-v2`  |
| `read2me-mpnet-base-v2` | 8201 | `all-mpnet-base-v2` |

| Method | Path           | Purpose                                     |
| ------ | -------------- | ------------------------------------------- |
| POST   | `/similarity`  | Cosine similarity between two strings       |

Request body:

```json
{ "text1": "...", "text2": "..." }
```

Response:

```json
{ "similarity": 0.91 }
```

Similarity is cosine score in `[0,1]`. Higher = semantically closer. Used by the app's Semantic Rescue step: when WER fails, this score determines whether the clip is rescued (threshold configurable per service in app settings).
