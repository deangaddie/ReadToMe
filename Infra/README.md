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
| Whisper (GPU)        | `read2me-whisper`           | 9000 | GPU audio transcription for accuracy scoring                     |
| Whisper (CPU)        | `read2me-whisper-cpu`       | 9001 | CPU-only transcription fallback                                  |
| MiniLM-L6            | `read2me-minilm-l6`         | 8200 | Semantic similarity — MiniLM-L6-v2                               |
| MPNet-Base-v2        | `read2me-mpnet-base-v2`     | 8201 | Semantic similarity — all-mpnet-base-v2                          |

## GPU / VRAM note

Configured for RTX 3070 (8 GB VRAM). All containers are GPU-resident — running more than one at a time is not possible at this VRAM budget. Start and stop containers manually depending on what stage of the app you are working in.

| Container                   | When to run                                      |
| --------------------------- | ------------------------------------------------ |
| `read2me-llama`             | LLM tasks (script processing)                    |
| `read2me-chatterbox`        | TTS with expression control / voice cloning      |
| `read2me-chatterbox-turbo`  | TTS with paralinguistic tags                     |
| `read2me-qwen3-tts`         | TTS with voice design from text description      |
| `read2me-qwen3-tts-base`    | TTS with voice cloning from reference audio      |
| `read2me-voxcpm2`           | TTS with VoxCPM2 voice cloning                   |
| `read2me-whisper`           | GPU audio transcription / accuracy scoring       |
| `read2me-whisper-cpu`       | CPU transcription when GPU is occupied           |
| `read2me-minilm-l6`         | Semantic similarity (no GPU — CPU only)          |
| `read2me-mpnet-base-v2`     | Semantic similarity (no GPU — CPU only)          |

> **Note:** Whisper (`base.en`) is small enough to run alongside a Chatterbox container. During audio generation, start both `read2me-whisper` and the relevant Chatterbox container together. The semantic similarity containers (`minilm-l6`, `mpnet-base-v2`) are CPU-only and can run at any time.

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

## llama.cpp

Custom image built from `Dockerfile.llama` using a fork with TurboQuant KV cache support (`feature/turboquant-kv-cache`). Serves an OpenAI-compatible API (`/v1/chat/completions`, `/v1/models`).

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

## Whisper (GPU)

Uses `onerahmet/openai-whisper-asr-webservice:latest-gpu` (GPU-enabled). Downloads the configured model on first API request.

Model and cache persist in `whisper_cache` volume — restarts skip the download.

Key environment variables (set in `docker-compose.yml`):

| Variable         | Default                | Notes                                                              |
| ---------------- | ---------------------- | ------------------------------------------------------------------ |
| `ASR_ENGINE`     | `openai_whisper`       | Also supports `faster-whisper`, `whisperx`                         |
| `ASR_MODEL`      | `base.en`              | English-only model. Use `base`, `small`, `medium` for multilingual |
| `ASR_MODEL_PATH` | `/root/.cache/whisper` | Persisted via `whisper_cache` volume                               |
| `ASR_DEVICE`     | `cuda`                 | GPU inference                                                      |

Swagger UI available at `http://localhost:9000` when running.

## Whisper (CPU)

Same image as GPU Whisper (`onerahmet/openai-whisper-asr-webservice:latest`, no `-gpu` tag). CPU inference. Use when GPU is occupied by a TTS container.

Port `9001`. Same API and environment variables; `ASR_DEVICE=cpu`.

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
