# models/

Local model storage for the llama.cpp and Whisper.CPP containers. Files here are bind-mounted into the containers at `/models`.

Model files (`.gguf`, `.bin`, etc.) are excluded from git — this folder exists only to hold them on disk.

## GGUF location

The GGUF directory mounted into `llama` is configurable so the files can be shared with other projects. Set `GGUF_MODELS_DIR` in `Infra/.env` (see `Infra/.env.example`); it defaults to `./models` when unset. The container path is always `/models`, so `llama/config/models.ini` never changes. Bind mounts resolve at container start — changing this needs no image rebuild, only `docker compose up -d llama`.

The Whisper model stays in this folder regardless of `GGUF_MODELS_DIR`.

## Used by

**Service:** `llama` in [docker-compose.yml](../docker-compose.yml)  
**Mount:** `${GGUF_MODELS_DIR:-./models}:/models:ro` (read-only inside container)  
**Referenced via:** preset config in `llama/config/models.ini`

Switch model without restart via **autoload** — name the target model in an inference request and `--models-max 1` evicts the currently loaded model (the request blocks until the new model loads):

```bash
curl http://localhost:8080/v1/chat/completions \
  -d '{"model":"gemma-26b","messages":[{"role":"user","content":"hi"}],"max_tokens":1}'
```

`POST /v1/models` does **not** switch models on the pinned fork build (it 404s). Probe the loaded preset with `GET /v1/models` and read each item's `status.value` (`unloaded`/`loading`/`loaded`).

Or restart the service:

```bash
docker compose up -d llama
```

### Whisper.CPP

The hardened `whisper` service mounts only
`ggml-base.en.bin` at `/models/ggml-base.en.bin` (read-only). Provision it with
the committed verifier rather than downloading it from a container:

```powershell
.\Infra\scripts\provision-whisper-model.ps1
```

The companion `whisper-models.sha256` manifest pins the artifact's immutable
source revision, SHA-256 and byte length. Do not manually replace this file;
update and review the manifest first, then rerun the provisioner.

## Required files

Each preset in `llama/config/models.ini` points at a `.gguf` file expected in this folder. Download whichever preset(s) you intend to use (the default served preset is `gemma-26b`). Filenames must match the `model = /models/...` path in the preset exactly.

| Preset | Expected file |
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

The `gemma-26b` default is a Mixture-of-Experts model (~4B active params per token), runnable on 8–12 GB VRAM with CPU offload of expert layers (`n-cpu-moe`).

Download GGUF quants from HuggingFace — search the model name and pick the matching quant (Q4_K_M / Q4_K_XL), then place it here:

```bash
pip install huggingface-hub
huggingface-cli download <repo-id> <filename.gguf> --local-dir ./Infra/models
```

## TurboQuant KV cache

This setup uses `--cache-type-k turbo4 --cache-type-v turbo3` (Google DeepMind TurboQuant).
This requires the turboquant build of llama.cpp — standard llama.cpp builds do not support these cache types.
See [llama-cpp-turboquant](https://github.com/TheTom/llama-cpp-turboquant).
