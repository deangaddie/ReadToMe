# One qwen-28b preset for every LLM task, with a preset-level thinking budget

## Status

accepted (2026-09-30). Decided in the LLM Model Upgrade map (`.scratch/llm-model-upgrade/`, tickets 19–24).

## Context

Attribution ran as a 3-step escalation chain over two models: `qwen-28b` thinking off → `gemma-26b_QAT` thinking
off → `qwen-28b` thinking on. The active config (discovery, voice plans and prompts, book edits) was Gemma QAT. The
llama presets used llama.cpp's default `ub 512`, and every LLM config left sampling unset.

A bench scored candidates against a labelled ground-truth set (310 items thinking off, 228 thinking on), gated on
scattered wrong, whole-scene wrong and unknown against the old `qwen-28b`:

- **Thinking off:** `ub`/`b` 2048 with `n-cpu-moe 30` made `qwen-28b` 19 % faster at the same quality. Qwen3.6
  vendor sampling added +7 correct and −8 unknown. Gemma QAT failed (unknown 104); Qwen3.6-35B MTP passed with the
  most wrong answers; Nemotron failed.
- **Thinking on:** no candidate was faster at baseline quality, because thinking decode dominates. A 4096-token
  thinking budget kept quality and removed runaways (the unbounded preset hit the 8192 cap about once per 2 runs).
  2048 was 41 % faster but turned unknowns into wrong answers; a budgeted Gemma was 53 % faster but left +20
  unknowns.
- llama.cpp enforces a budget per preset (`reasoning-budget`) or per request (`thinking_budget_tokens`); the app
  sends neither.

## Decision

- **One preset, `qwen-28b`**: the Qwen3.6-28B REAP Q4_K_M model with `n-cpu-moe 30`, `b`/`ub` 2048, q8_0 KV, vendor
  sampling as the preset fallback, and **`reasoning-budget = 4096`**.
- **Two LLM configs on it**, one per thinking mode, each sending the vendor temperature / top-p / presence for its
  mode (0.7 / 0.8 / 1.5 off, 1.0 / 0.95 / 1.5 on), MaxTokens 8192, batch 4, Full style.
- **A 2-step chain**: attribution (off) → thinking (on). The Gemma middle step is dropped.
- **The attribution config is the active config**, so discovery, voice plans and prompts, and book edits run on
  the same preset.
- **The budget lives in the preset**, not in the app: a new config field would need a migration, request-builder
  work and both UIs, for a cap that the one shared preset already gives.

## Consequences

- Escalating to the thinking step needs no model reload; the whole LLM workload stays on one resident model.
- The budget also caps voice plans and thinking-on discovery. Measured: voice plans used 1.1k–2.9k thinking tokens,
  discovery ≈ 3.9k. If a future task needs longer thinking, add an unbudgeted twin preset for it, or move the budget
  to a per-config request field.
- Sampling per mode now lives in the configs; the preset's values only apply to a config that sets none.
- llama mlocks the offloaded experts in the WSL2 VM, which is sized to 16 GB for this preset (`Infra/README.md`,
  "Host / WSL memory").
- The other bench models (Gemma 4 26B, Qwen3.6-35B MTP, Nemotron, Ornith) were removed from `models.ini`.
- In bench reports before 2026-09-30, `qwen-28b` names the old preset (production sampling, `ub 512`, no budget).
