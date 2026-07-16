# Infra

Docker infrastructure for ReadToMe AI services.

## Structure

```text
Infra/
├── docker-compose.yml          # All AI service containers
├── Dockerfile.llama            # llama.cpp server (TurboQuant fork, multi-model preset)
├── Dockerfile.chatterbox       # Chatterbox TTS image (standard + turbo variants)
├── Dockerfile.qwen3            # Qwen3 TTS image
├── llama/
│   ├── entrypoint.sh           # Starts llama-server with model presets
│   └── config/
│       └── models.ini          # Model preset definitions
├── chatterbox/                 # Chatterbox TTS FastAPI apps
│   ├── app.py                  # Standard Chatterbox TTS
│   └── app_turbo.py            # Turbo-only Chatterbox TTS (paralinguistic tags)
├── qwen3/                      # Qwen3 TTS FastAPI app
└── models/                     # GGUF model files (bind-mounted, not committed)
```

## Services

| Service              | Container                   | Port | Purpose                                                          |
| -------------------- | --------------------------- | ---- | ---------------------------------------------------------------- |
| llama.cpp            | `read2me-llama`             | 8080 | LLM — character extraction, script classification                |
| Chatterbox TTS       | `read2me-chatterbox`        | 8000 | TTS — standard model, expression instructions, voice cloning     |
| Chatterbox Turbo     | `read2me-chatterbox-turbo`  | 8001 | TTS — turbo model, paralinguistic tags only, voice cloning       |
| Qwen3 TTS            | `read2me-qwen3-tts`         | 8100 | TTS — voice design from text description, no reference audio     |
| Qwen3 TTS Base       | `read2me-qwen3-tts-base`    | 8101 | TTS — voice cloning from reference audio + transcript            |
| VoxCPM2              | `read2me-voxcpm2`           | 8003 | TTS — VoxCPM2 voice cloning                                      |
| Whisper.CPP          | `read2me-whisper`           | 9000 | CPU-only transcription for WER and word-level alignment          |
| MiniLM-L6            | `read2me-minilm-l6`         | 8200 | Semantic similarity — MiniLM-L6-v2                               |
| MPNet-Base-v2        | `read2me-mpnet-base-v2`     | 8201 | Semantic similarity — all-mpnet-base-v2                          |

## GPU / VRAM note

Configured for RTX 3070 (8 GB VRAM). GPU-resident services cannot generally run together at this VRAM budget. CPU-only Whisper and the semantic-similarity services can run alongside a GPU service.

| Container                   | When to run                                      |
| --------------------------- | ------------------------------------------------ |
| `read2me-llama`             | LLM tasks (script processing)                    |
| `read2me-chatterbox`        | TTS with expression control / voice cloning      |
| `read2me-chatterbox-turbo`  | TTS with paralinguistic tags                     |
| `read2me-qwen3-tts`         | TTS with voice design from text description      |
| `read2me-qwen3-tts-base`    | TTS with voice cloning from reference audio      |
| `read2me-voxcpm2`           | TTS with VoxCPM2 voice cloning                   |
| `read2me-whisper`           | CPU transcription for WER and word-level alignment |
| `read2me-minilm-l6`         | Semantic similarity (no GPU — CPU only)          |
| `read2me-mpnet-base-v2`     | Semantic similarity (no GPU — CPU only)          |

> **Note:** Whisper.CPP and the semantic similarity containers are CPU-only and can run alongside a Chatterbox container.

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

Custom image built from `Dockerfile.llama` using the TurboQuant KV-cache fork pinned at commit `4503343ffc05c09f6b50c309c8ecbabb49c66ea2`. Serves an OpenAI-compatible API (`/v1/chat/completions`, `/v1/models`).

The fork is frozen until a failure forces a change: there is no update cadence or Dependabot entry. Before any bump, diff the candidate SHA against its upstream `ggml-org/llama.cpp` merge-base and review the fork-specific delta. Any change to networking, file I/O outside the model path, or build scripts blocks the bump.

Model presets are defined in `llama/config/models.ini`. Multiple models can be configured; only one is loaded at a time (`--models-max 1`). Switch without restart:

```bash
curl -X POST http://localhost:8080/v1/models -d '{"model":"gemma-26b"}'
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

## Chatterbox TTS

Two containers, same `Dockerfile.chatterbox`, different entry modules selected via `APP_MODULE` build arg.

**Voice cloning is required** — no built-in voices. All requests must include `reference_audio` (WAV or MP3).

### Standard — port 8000 (`read2me-chatterbox`)

Loads `ChatterboxTTS`. **No free-text instruction channel** — expression comes from `exaggeration`/`temperature`, not an `instructions` param (removed).

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/health` | Liveness check, reports device |
| POST | `/tts` | Speech with voice cloning |

#### POST /tts

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `text` | string | yes | Plain text — no paralinguistic tags |
| `reference_audio` | file | yes | WAV/MP3 for voice cloning |
| `exaggeration` | float | no | 0–1, expressiveness (default 0.5) |
| `cfg_weight` | float | no | 0–1, guidance weight (default 0.5) |
| `temperature` | float | no | Sampling randomness (default 0.8) |
| `min_p` | float | no | Nucleus sampling floor (default 0.05) |
| `top_p` | float | no | Nucleus sampling ceiling (default 1.0) |
| `repetition_penalty` | float | no | Penalizes repeated tokens (default 1.2) |

### Turbo — port 8001 (`read2me-chatterbox-turbo`)

Loads `ChatterboxTurboTTS` only. Use when text contains paralinguistic tags. Does not support `exaggeration`, `cfg_weight`, or `instructions`. **English-only** — no free-text instruction channel; expression comes from inline tags.

| Method | Path         | Purpose                         |
| ------ | ------------ | ------------------------------- |
| GET    | `/health`    | Liveness check, reports device  |
| POST   | `/tts/turbo` | Speech with paralinguistic tags |

#### POST /tts/turbo

| Field                | Type   | Required | Notes                                         |
| -------------------- | ------ | -------- | --------------------------------------------- |
| `text`               | string | yes      | Text with paralinguistic tags                 |
| `reference_audio`    | file   | yes      | WAV/MP3 for voice cloning (~10 seconds ideal) |
| `temperature`        | float  | no       | Sampling temperature (default 0.8)            |
| `repetition_penalty` | float  | no       | Penalizes repeated tokens (default 1.2)       |

Supported paralinguistic tags: `[laugh]` `[chuckle]` `[sigh]` `[cough]` `[clear throat]` `[gasp]` `[groan]` `[sniff]` `[shush]`

Both containers return `audio/wav`.

## Qwen3 TTS

Custom image built from `Dockerfile.qwen3`. Exposes `Qwen3-TTS-12Hz-1.7B-VoiceDesign` through a FastAPI wrapper in `qwen3/app.py`.

Generates voices from text descriptions — no reference audio required.

| Method | Path      | Purpose                                       |
| ------ | --------- | --------------------------------------------- |
| GET    | `/health` | Liveness check, reports device and model name |
| POST   | `/tts`    | Text-to-speech generation using Qwen3 TTS     |

| Field                | Type   | Required | Notes                                                  |
| -------------------- | ------ | -------- | ------------------------------------------------------ |
| `text`               | string | yes      | Plain text to synthesize                               |
| `voice_description`  | string | yes      | Text description of the desired voice                  |
| `language`           | string | no       | Default `"auto"` (auto/en/zh/ja/ko/de/fr/ru/pt/es/it)  |
| `temperature`        | float  | no       | HF sampling kwarg, omitted when unset                  |
| `top_p`              | float  | no       | HF sampling kwarg, omitted when unset                  |
| `top_k`              | int    | no       | HF sampling kwarg, omitted when unset                  |
| `repetition_penalty` | float  | no       | HF sampling kwarg, omitted when unset                  |
| `max_new_tokens`     | int    | no       | HF sampling kwarg, omitted when unset                  |

Returns `audio/wav`.

## Qwen3 TTS Base

Custom image built from `Dockerfile.qwen3` (same image, `app_base` module). Exposes `Qwen3-TTS` (Base model) through `qwen3/app_base.py` for voice cloning from a reference audio clip and its transcript.

| Method | Path      | Purpose                                       |
| ------ | --------- | --------------------------------------------- |
| GET    | `/health` | Liveness check, reports device and model name |
| POST   | `/tts`    | Voice cloning with reference audio            |

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `text` | string | yes | Plain text to synthesize |
| `reference_audio` | file | yes | WAV/MP3 voice sample for cloning |
| `voice_transcript` | string | yes | Transcript of the reference audio |
| `language` | string | no | Default `"auto"` (auto/en/zh/ja/ko/de/fr/ru/pt/es/it) |
| `temperature` | float | no | HF sampling kwarg, omitted when unset |
| `top_p` | float | no | HF sampling kwarg, omitted when unset |
| `top_k` | int | no | HF sampling kwarg, omitted when unset |
| `repetition_penalty` | float | no | HF sampling kwarg, omitted when unset |
| `max_new_tokens` | int | no | HF sampling kwarg, omitted when unset |

Returns `audio/wav`.

## VoxCPM2

Native inference server (`voxcpm2/server.py`), no vLLM backend. Wraps `openbmb/VoxCPM2` for voice cloning.

Port `8003`. Two-step protocol — upload the reference clip once, then stream generation referencing its `file_id`.

| Method | Path            | Purpose                                         |
| ------ | --------------- | ------------------------------------------------ |
| GET    | `/health`       | Liveness check, reports model-loaded state      |
| POST   | `/upload-audio` | Upload reference audio, returns a `file_id`     |
| POST   | `/api/stream`   | Streaming generation using an uploaded `file_id`|

### POST /upload-audio

Multipart. Field `file` (WAV/MP3/FLAC/OGG/M4A). Response:

```json
{ "file_id": "<uuid>" }
```

Uploads are cached server-side and expire after `UPLOAD_TTL_SECONDS` (default 3600s).

### POST /api/stream

JSON body. Response is `application/octet-stream`, a sequence of framed binary messages: **1 type byte** + **4-byte little-endian length** (`<I`) + payload. Type `0` = JSON control frame (`meta`/`done`/`error`); type `1` = raw `float32` PCM chunk.

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `text` | string | yes | Plain text to synthesize |
| `control` | string | no | Prepended as `(control)text` — style/emotion hint |
| `reference_wav_path` | string | no | `file_id` from `/upload-audio` (voice cloning) |
| `cfg_value` | float | no | Classifier-free guidance weight (default 2.0) |
| `inference_timesteps` | int | no | Diffusion steps (default 10) |
| `min_len` | int | no | Minimum output length (default 2) |
| `max_len` | int | no | Maximum output length (default 4096) |
| `normalize` | bool | no | Loudness normalization (default false) |
| `denoise` | bool | no | Denoise reference audio (default false) |
| `retry_badcase` | bool | no | Retry generation on bad-case detection (default true) |
| `retry_badcase_max_times` | int | no | Max retries (default 3) |
| `retry_badcase_ratio_threshold` | float | no | Bad-case detection threshold (default 6.0) |

Stream sequence: one `meta` frame (`{"type": "meta", "sample_rate": ...}`) → N audio frames (raw float32 PCM) → one `done` frame (`{"type": "done", "chunks": N}`), or an `error` frame if generation fails.

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
