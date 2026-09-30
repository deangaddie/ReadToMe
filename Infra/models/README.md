# models/

Local model storage for the llama.cpp, audio.cpp and Whisper.CPP containers. Files here are bind-mounted into the containers at `/models`.

Model files (`.gguf`, `.bin`, etc.) are excluded from git — this folder exists only to hold them on disk.

## GGUF location

The GGUF directory mounted into `llama` and `audiocpp` is configurable so the files can be shared with other projects. Set `GGUF_MODELS_DIR` in `Infra/.env` (see `Infra/.env.example`); it defaults to `./models` when unset. The container path is always `/models`, so `llama/config/models.ini` and `audiocpp/server.json` never change. Bind mounts resolve at container start — changing this needs no image rebuild, only `docker compose up -d llama audiocpp`. The audio.cpp TTS GGUFs and where to get them are listed in [../README.md](../README.md#audiocpp-tts).

The Whisper model stays in this folder regardless of `GGUF_MODELS_DIR`.

## Used by

**Service:** `llama` in [docker-compose.yml](../docker-compose.yml)  
**Mount:** `${GGUF_MODELS_DIR:-./models}:/models:ro` (read-only inside container)  
**Referenced via:** preset config in `llama/config/models.ini`

Switch model without restart via **autoload** — name the target model in an inference request and `--models-max 1` evicts the currently loaded model (the request blocks until the new model loads):

```bash
curl http://localhost:8080/v1/chat/completions \
  -d '{"model":"qwen-28b","messages":[{"role":"user","content":"hi"}],"max_tokens":1}'
```

`POST /v1/models` did **not** switch models on the old fork build (it 404ed) and is untested on upstream; the app uses autoload. Probe the loaded preset with `GET /v1/models` and read each item's `status.value` (`unloaded`/`loading`/`loaded`).

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

Each preset in `llama/config/models.ini` points at a `.gguf` file expected in this folder. Download whichever preset(s) you intend to use; the app needs only `qwen-28b` (see [../README.md](../README.md#attribution-preset--qwen-28b)). Filenames must match the `model = /models/...` path in the preset exactly.

| Preset | Expected file |
| --- | --- |
| `qwen-28b` | `Qwen3.6-28B-REAP20-A3B-Q4_K_M.gguf` |
| `qwen-9b` | `Qwen3.5-9B-UD-Q4_K_XL.gguf` |
| `qwen-4b` | `Qwen3.5-4B-UD-Q4_K_XL.gguf` |
| `gemma-12b_QAT` | `gemma-4-12B-it-qat-UD-Q4_K_XL.gguf` |
| `gemma-4b` | `gemma-4-E4B-it-UD-Q4_K_XL.gguf` |
| `lamma-3.1-8b` | `Llama-3.1-8B-Instruct-Q6_K.gguf` |
| `lamma-3.2-3b` | `Llama-3.2-3B-Instruct-Q8_0.gguf` |

`qwen-28b` is a Mixture-of-Experts model (~3B active params per token), runnable on 8 GB VRAM with CPU offload of
expert layers (`n-cpu-moe`). The offloaded experts are mlocked in the WSL VM, which must be sized for them
(16 GB; see [../README.md](../README.md#host--wsl-memory)).

A shared `GGUF_MODELS_DIR` may hold files other projects use (e.g. LocalLLM's `gemma-4-26B-A4B-it-UD-Q4_K_M.gguf`);
check before deleting a file no ReadToMe preset names.

Download GGUF quants from HuggingFace — search the model name and pick the matching quant (Q4_K_M / Q4_K_XL), then place it here:

```bash
pip install huggingface-hub
huggingface-cli download <repo-id> <filename.gguf> --local-dir ./Infra/models
```
