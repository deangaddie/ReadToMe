# Chapter-pass attribution: an opt-in prompt style on a second preset

## Status

accepted (2026-10-03). Decided in the attribution-grammar spec (`.scratch/attribution-grammar/`, tickets 01–08).
Amends [ADR 0011](0011-one-qwen-preset-with-a-thinking-budget.md): `qwen-28b` stays the preset for every LLM task
except a Chapter-style attribution rung, which runs on a second preset, `gemma-12b`. The default attribution path is
unchanged.

## Context

The attribution-options spike (`ReadToMe-attrib`, `.scratch/attribution-options/REPORT.md`) compared answer shapes
and models for character attribution. Its winner asks one dialog item at a time through the whole chapter so far,
on a 12B model with a grammar that restricts the speaker to the roster. In the lab, on 333 held-out items, it scored
320 correct / 6 unknown / 7 wrong in 181 s, against 302 / 8 / 23 in 710 s for today's path.

Today's path (the `Full` style on `qwen-28b`, ADR 0011) asks for a JSON answer over a chunk of paragraphs with 6
paragraphs before and 4 after as context. The model may name anyone, and the final chain rung creates a Character
for an unlisted name.

The app build was measured with the spike harness on frozen copies of the workspace. The realistic set has 643
labelled items in 7 chapters across 5 books, with stored or hand-written rosters, so some speakers are missing from
the roster:

| Path | Correct | Unknown | Wrong | Wall time |
| --- | --- | --- | --- | --- |
| Current: `qwen-28b`, Full | 535 | 44 | 64 | 1516 s |
| Chapter style: `gemma-12b` | 566 | 63 | 14 | 340 s |
| Chapter style, unknowns escalated to `qwen-28b` · Full · thinking | 570 | 34 | 39 | 2045 s |

Wrong answers fell by 78 % and the run was 4.5× faster. Rules-discover created 4 real characters in the one
chapter whose roster lacked them, and no phantoms. Escalating the unknowns is a bad trade: it turned 29 unknowns into
4 more correct and 25 more wrong. Nearly all of those are in the chapter whose speakers rules-discover can't find,
where `qwen-28b` names a listed character instead. It also took 6× the time, because of the model swaps and thinking.

With the hand-made complete rosters (one run each), the Chapter style scored 262 / 44 / 4 in 156 s in-sample
(310 items; current path 256 / 23 / 31 in 704 s) and 320 / 5 / 8 in 182 s held-out (333 items; current path
302 / 8 / 23 in 710 s).

## Decision

- **An opt-in prompt style.** `AttributionPromptStyle.Chapter = 2`, selected like `Simple`: on an LLM config or on
  one chain rung. `StyleRoutedChainStep` sends a Chapter rung to `ChapterAttributionStep` and every other rung to
  `CharacterAttributionService`. `Full` and `Simple` are unchanged, and **the default stays `Full`** until Dean
  decides to switch.
- **A second llama preset, `gemma-12b`**, with 16384 context. A Chapter config names it. Every other task (discovery,
  voice plans and prompts, book edits, `Full`/`Simple` rungs) stays on `qwen-28b`. This is where ADR 0011's "one
  preset for every LLM task" no longer holds. The recommended chain is one rung, `[gemma-12b · Chapter]`: its
  unknowns go to manual review. A second `qwen-28b` rung adds more wrong names than right ones (see Context).
- **Grammar-restricted roster names.** The answer is constrained by a GBNF grammar to `Name | delivery`. Name is a
  roster name or `Unknown`, and delivery is an optional cue of up to 40 characters. Thinking is off, temperature is 0,
  and `max_tokens` is 48, whatever the config says. **The model can never create a Character on this path**; there
  is no free-text escape.
- **Rules-discover is the only creator on this path.** Before each chapter, a deterministic said-tag scan
  (`SpeechTagRules.DiscoverNames`) creates each named speaker missing from the roster, through `CharacterResolver`.
  A rules pre-tag (`SpeechTagRules.TagChapter`) fixes the speaker of explicitly tagged lines, which then get a
  voice-only call.
- **The cache contract.** The system message (instructions, book, roster) is built once per chapter. The user
  message is the chapter so far, plus 4 look-ahead paragraphs, plus the question. It is append-only between calls:
  the only change to an earlier line is an inserted `{Name}` label for an item just answered. Nothing else uses the
  LLM slot inside a chapter. Long chapters are front-trimmed in halves, and the trim point then stays fixed, so llama
  reuses the prefix and prefills only a few hundred tokens per call.
- **Unknowns follow the chain as before.** An `Unknown` answer makes the paragraph suspect. The existing walk re-asks
  it on the next rung after the whole step-0 pass, or sends it to manual review in a one-rung chain (recommended).

Recorded here because the same branch changed it app-wide: **an LLM 4xx response (except 408 and 429) is a request
failure, not a service-health failure.** It returns `Failed`, is not reported to the AI-service health monitor, and
does not feed the watchdog. Before, a managed endpoint's 400 became `ServiceUnavailable`. That disabled the chapter
pass's context-overflow retry, and every bad request counted toward a container restart. The queue now fails such a
paragraph immediately instead of requeueing it with backoff. This applies to every LLM caller: `Full`/`Simple`
attribution, discovery, book edits and voice design.

## Consequences

- **Rendering divergence.** On the `Full` path a partly attributed paragraph is never sent as context. The chapter
  pass renders the whole chapter, with unlabelled items shown without a label. The lab measured exactly this
  layout.
- **R6: a suspect paragraph's named items wait for its final answer.** Escalation re-asks the whole paragraph, and a
  later rung that ties at the `Unknown` rank wins. So a Full rung can drop names the Chapter rung got right in the
  same paragraph. This is today's chain semantics, kept for v1 and pinned by a test. A per-item merge is the
  follow-up if the measurement shows it costs correct lines.
- **Model swaps.** A queue drain with the recommended chain switches to `gemma-12b` once and back to `qwen-28b` once,
  because the walk runs step 0 over the whole queue first. Another LLM task during a chapter pass switches the model
  and loses the prompt cache. That costs speed, not correctness.
- **Phantom characters.** Rules-discover can create a Character from a said-tag that names a place or an opener the
  filters miss. It creates without asking, and the user merges phantoms in the cast list.
- **Settings.** The settings page offers Chapter as a fast variant only: a thinking Chapter rung would run exactly
  like the fast one.
- **Request shape.** `LlmRunRequest` carries an optional system prompt, a GBNF grammar (exclusive with a JSON schema)
  and a short display prompt for the hub. `LlmRunResult` carries the last `timings`, which the pass logs per request
  as `prompt_n` / `cache_n`.
