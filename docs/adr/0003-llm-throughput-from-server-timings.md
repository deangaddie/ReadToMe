# 3. LLM throughput comes from server timings, never client estimates

Date: 2026-07-17

## Status

Accepted. Amended 2026-07-17 — see [Amendment](#amendment-2026-07-17-the-completion-runner-landed-first).

## Context

The app displayed a tok/s figure for LLM requests that was invented. `StreamMetrics` estimated tokens at `chars ÷ 4` and divided by a `Stopwatch` measuring wall-clock time around the stream. Both inputs were wrong: the token count was a guess, and the elapsed time included prompt evaluation and — on a cold config — model load, which on an 8 GB RTX 3070 swapping a 26b GGUF is substantial. The resulting number could be off by a wide margin in either direction, and nothing marked it as approximate.

Research against the pinned llama.cpp fork (`TheTom/llama-cpp-turboquant` @ `4503343`) established that the server already sends exactly what we need, and that we were discarding it:

- `timings` rides the final stream chunk at the JSON root, ungated by any request option. We receive it today.
- `timings.predicted_ms` spans precisely first-token → stream-end: `t_start_generation` is stamped at `n_decoded == 1`, and model load is in neither counter.
- `OpenAiStreamParser.ParseChunk` reads only `choices[0].delta` and returns `null` for any payload without one. The metrics chunk is **delta-less**, so all three of its early-returns discard it.
- `usage` is opt-in via `stream_options.include_usage`; `timings` is not — they have different gates.
- Per-chunk timings are available via `timings_per_token: true`, but the values are **cumulative-running, not instantaneous**.
- An aborted stream sends no final chunk at all — no `timings`, not even `data: [DONE]`.

## Decision

**Every displayed throughput figure derives from llama.cpp's `timings`. No estimation.** `StreamMetrics` is deleted, not fixed.

Arithmetic on measured values is still measured: a run total is `Σ predicted_n ÷ Σ predicted_ms`, and a live rate is consecutive chunks' cumulative readings differenced. The ban is on *inventing* inputs, not on computing with real ones.

Consequently:

- **`timings_per_token: true` and `stream_options.include_usage: true` are both enabled.** The former is the only source of a mid-stream rate and the only way an aborted request retains a measurement; the latter makes `TokensIn` a real `usage.prompt_tokens` instead of a guess.
- **Absence is first-class.** Where a backend sends no `timings`, surfaces show nothing. Every timings-derived value is nullable end to end (`LlmTimings?` → `double?` → `double?`), because llama.cpp's unguarded divide-by-zero serializes as `null` on a fully cached prompt. `0 tok/s` and "unknown" must never be the same value.
- **Rates are recomputed from `predicted_n ÷ predicted_ms`, not read from the server's `predicted_per_second`.** A request rate and a run rate must be arithmetically consistent, which they cannot be if one is server-rounded and the other summed.
- **`StreamCompleted`'s `ElapsedSeconds` becomes `GenerationMs`** — a rename, not a unit change. The old field was wall-clock; the new one is server-measured generation with load and queue wait excluded. A same-named field with a changed meaning is the trap this avoids. (Published from one site, not eight — see the amendment.)
- **Aborted requests count**, using their last received measurement. It is real work, really measured; excluding it would make totals less accurate, not more.

## Consequences

**Good.** The numbers become true. Load time can no longer inflate a slow-looking model, and token counts are the server's own. `cached_tokens` visibility arrives free. The `TimingsAccumulator` is pure — fed arrival timestamps rather than reading a clock — so throughput is testable with plain arrays and no fake clock.

**Costs.** `timings` is serialized on every token, accepted for the live rate and abort coverage. `StreamCompleted` gains nullability and a renamed field — at **one** publish site (see the amendment; the figure was eight when this ADR was written). Every consumer must handle absence rather than rendering a fallback.

**Traps for the next reader.**

- Per-chunk timings are **cumulative**. An accumulator that sums them double-counts every token. `Add` replaces the latest reading; it never accrues.
- `prompt_ms` is re-assigned at first token, so it **includes the first token's decode** — it is time-to-first-token from prompt start, not the bare prompt pass.
- `prompt_ms + predicted_ms` ≠ request latency. Queue/scheduling wait before `t_start_process_prompt` is unmeasured.
- The metrics chunk is **delta-less**: `timings`/`usage` ride the `finish_reason` chunk, whose `delta` is an empty object. A fixture that hangs them on a chunk with a *populated* delta encodes a shape llama.cpp **never emits**, and a parser test written against it passes while the parser drops the real chunk. The repo's own fixtures (`Infra/ContainerHealth/tests/fixtures/proxy-harness.mjs`, `tests/llama.unit.spec.ts`) carried exactly that error until it was corrected; do not reintroduce it.

## Alternatives rejected

**Keep client-side estimation for the live ticker (hybrid).** The original plan. Rejected once `timings_per_token` proved a mid-stream rate could be server-grounded — there was no longer any reason to invent a number at any point.

**Fall back to a marked estimate when timings are absent.** Rejected: a marked wrong number is still a wrong number, and the mark is easy to miss. Showing nothing is unambiguous.

**Meter by decorating `ILlmClient` below the runner.** Would catch every caller with no migration, but meters *transport*: run boundaries and config identity live above it in `LlmRunRequest` and would have to be re-derived from data the transport layer cannot see.

## Amendment 2026-07-17: the completion runner landed first

This ADR was written expecting to meter a codebase where seven feature services each carried their own duplicated streaming envelope, and each built `StreamCompleted` from its own `StreamMetrics`. **That is no longer true.** All five `completion-runner` slices completed before any throughput work began, so every LLM-calling service now goes through `ILlmCompletionRunner`.

No decision above changes. Three stated costs shrink:

- **`StreamCompleted`'s reshape is a one-site change**, not eight. `LlmCompletionRunner` is the only publisher; `LlmStreamView` holds the only consumers. It is not a wide refactor and needs no expand–contract.
- **`StreamMetrics` has two users**, not eight: the runner, and `Pages/LlmSettings.razor`. Deleting it means changing those two.
- **`TimingsAccumulator` mirroring `StreamMetrics`' call shape is no longer load-bearing.** The mirror was justified as keeping seven unmigrated envelopes a mechanical swap; there are none. The shape stands on its own merits — do not defend it on the migration argument.

`Pages/LlmSettings.razor` remains the sole gap the runner effort did not close: still a direct `ILlmClient` caller, still building its own `StreamMetrics`, still publishing no events. Migrating it is owned by the `llm-throughput` effort. `DockerAiServiceRegistry`'s warm-up call is also a direct `ILlmClient` caller and is deliberately left unmetered — it is a container probe, not a Throughput Run.
