# models/

Local model storage for the llama.cpp container. Files here are bind-mounted into the container at `/models`.

Model files (`.gguf`, `.bin`, etc.) are excluded from git — this folder exists only to hold them on disk.

## Used by

**Service:** `llama` in [docker-compose.yml](../docker-compose.yml)  
**Mount:** `./Infra/models:/models:ro` (read-only inside container)  
**Referenced via:** preset config in `llama/config/models.ini`

Switch model without restart:

```bash
curl -X POST http://localhost:8080/v1/models -d '{"model":"gemma-26b"}'
```

Or restart the service:

```bash
docker compose up -d llama
```

## Current target model

**Gemma 4 26B A4B** — a Mixture-of-Experts model. Only ~4B parameters active per token, allowing it to run on consumer GPUs with 8–12 GB VRAM using CPU offloading for expert layers.

Download from HuggingFace:

- [Model page](https://huggingface.co/google/gemma-4-26b-a4b-GGUF) — look for a GGUF quantized variant (Q4_K_M or similar)

Download with `huggingface-cli`:

```bash
pip install huggingface-hub
huggingface-cli download google/gemma-4-26b-a4b-GGUF --local-dir ./Infra/models
```

Or directly via browser from the Files tab on the model page above.

## TurboQuant KV cache

This setup uses `--cache-type-k turbo4 --cache-type-v turbo3` (Google DeepMind TurboQuant).
This requires the turboquant build of llama.cpp — standard llama.cpp builds do not support these cache types.
See [llama-cpp-turboquant](https://github.com/TheTom/llama-cpp-turboquant).
