#!/bin/bash
set -euo pipefail

MODELS_FILE="${LLAMA_MODELS_FILE:-/config/models.ini}"
HOST="${LLAMA_HOST:-0.0.0.0}"
PORT="${LLAMA_PORT:-8080}"
DEFAULT_MODEL="${LLAMA_DEFAULT_MODEL:-qwen-36b}"

BASE_ARGS=(
  "--host" "$HOST"
  "--port" "$PORT"
  "--models-preset" "$MODELS_FILE"
  "--models-max" "1"
)

exec /usr/local/bin/llama-server "${BASE_ARGS[@]}"
