# Constrained grammar for a choice of Edit operations

Researched 2026-10-19 for [issue 01](../issues/01-grammar-for-operation-union.md).

**Build checked.** `Infra/Dockerfile.llama` pins upstream `v0.5.0` = commit `7fe450e19305b828c199d602c23a8337aaa1f03b` (committed 2026-09-23; `Infra/README.md` L162). Every source link is at that commit; `T` = `https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b`. The build does not enable `LLAMA_LLGUIDANCE`, so the GBNF converter below is the one in use (`T/common/json-schema-to-grammar.cpp` L993-1000).

**No live probe.** `read2me-llama` was not running, and I did not start it. The source reading is enough for the converter questions. One follow-up probe is listed at the end.

## Answer

- **Yes. A discriminated union through `response_format` works on this build, with ~10 operations, provided the schema stays inside the subset described below.** `oneOf` and `anyOf` both compile to a plain GBNF alternation (`a | b | …`). `const` compiles to a literal. Each alternative pins `"operation"` to its own literal, so the grammar engine keeps one candidate parse per alternative only until the operation name is written, then a single one survives. The grammar is a few dozen to ~150 rules, far below any known size limit.
- **Recommendation: a union via `json_schema` (`response_format`), shaped as a fixed envelope with the union nested under one property:**
  ```json
  { "type": "object",
    "properties": {
      "reasoning": { "type": "string" },
      "plan": { "anyOf": [ { "$ref": "#/$defs/insert_node" }, { "$ref": "#/$defs/regex_replace" }, …,
                           { "$ref": "#/$defs/unsupported" }, { "$ref": "#/$defs/multiple_operations" } ] } },
    "required": ["reasoning", "plan"],
    "$defs": { "insert_node": { "type": "object",
                 "properties": { "operation": { "const": "insert_node" }, …args… },
                 "required": ["operation", …every arg…] }, … } }
  ```
  Each operation module contributes one `$defs` entry. Every property is `required`, and "optional" arguments are nullable (`["string","null"]`), which is the convention `EditProgramSchema` already follows.
- **A flat schema (one `operation` enum + one nullable argument object per operation) is weaker.** The grammar cannot tie the chosen `operation` to the non-null argument object. The model may fill the wrong one or none, and must also emit `null` for every other operation. It is the right fallback only if a probe shows the union misbehaving.
- **Hand-built GBNF is the riskiest route here**, not the safest. The "raw grammar is the only guaranteed route on qwen-28b" finding was about **whitespace** (compact output), not structure: the `response_format` grammar is already a hard constraint on structure. A raw `grammar` with thinking on also has to write the `<think>` block itself, or the answer lands in `reasoning_content` (see "Thinking" below). The Edit planner runs with thinking both on and off (`BookEditPlanner.PlanAsync(…, bool disableThinking, …)`), so it would need two hand grammars per registry. Keep raw GBNF for what it is used for now: the roster grammar, which runs with thinking off.

## Converter: what each JSON-schema feature does

Since mid-2026 the converter is two stages. `common/json-schema.cpp` parses the JSON into a typed AST (`common_chat_schema_*`), and `common/json-schema-to-grammar.cpp` turns the AST into GBNF. The **parser decides what is kept and what is dropped**. Both the `/completion` `json_schema` path and the chat `response_format` path use it: the Qwen3-Coder chat handler calls `p.schema(p.json(), "response-format", inputs.json_schema)` and `build_grammar` (`T/common/parsers/qwen3-coder.cpp` L80-82, L179-184; `T/common/json-schema-to-grammar.cpp` L1015-1028).

Precedence in `build_node` (`T/common/json-schema.cpp` L227-338). The first keyword that matches wins, and **every sibling keyword is silently ignored**:

| Order | Keyword | Result | Consequence for us |
|---|---|---|---|
| 1 | `$ref` | `build_ref` (L231-233, L99-114) | Siblings of `$ref` are dropped. Put descriptions and other keywords on the target, not beside the ref. |
| 2 | `oneOf` / `anyOf` | Both build the same `common_chat_schema_any_of` (L234-237) | `oneOf` is **not** exclusive; it is `anyOf`. That does no harm when each branch has a distinct `const`. **`properties`, `required` and `type` next to a `oneOf` are silently dropped.** This is the documented "can't mix `properties` w/ `anyOf`/`oneOf` in the same type" limitation (`T/grammars/README.md` L208, issue [#7703](https://github.com/ggml-org/llama.cpp/issues/7703)). So **never put `reasoning` and the union on the same node**: nest the union under a property (`plan`) as above. |
| 3 | `type: [..]` array | Expanded to `anyOf` of single-type copies (L244-256) | `["string","null"]` and `["integer","null"]` work, as `EditProgramSchema` already uses them. |
| 4 | `const` | Literal (L257-259; converter `_generate_constant_rule`, L824-826, L888-889) | Wins over `type` and `enum` on the same node. `{ "const": "insert_node" }` is enough; a `"type":"string"` beside it is ignored but harmless. |
| 5 | `enum` | Alternation of literals; any JSON values, `null` included (L260-270; converter L890-896) | Matches the existing `case_mode` `enum: [..., null]`. |
| — | `type: object` | `build_object` (L133-171) | See `additionalProperties` and ordering below. |
| — | `allOf` | Merges object properties and intersects enums (`_visit_all_of`, converter L832-875) | Not needed. Avoid it: an `anyOf` inside `allOf` turns its members into optional properties (test "mix of allOf, anyOf and $ref", `T/tests/test-json-schema-to-grammar.cpp` ~L1241-1290). |

Other keywords:

- **`$defs` / `definitions`.** Both work. `resolve_ref` walks any `#/…` JSON pointer in the same document (`T/common/json-schema.cpp` L74-97). Remote refs are a hard error: "only references into the same document are supported" (L104-106). A ref to a ref, and cycles, are handled: the key is reserved before building, so a cycle stops (L107-111). `T/grammars/README.md` L211 still lists "Nested `$ref`s are broken ([#8073](https://github.com/ggml-org/llama.cpp/issues/8073))", but #8073 was closed as completed on 2024-08-08, and the test "allOf with enum schema" covers the case. The README line is stale.
- **`discriminator`** (OpenAPI). It is never read; the identifier appears nowhere in `json-schema.cpp` or `json-schema-to-grammar.cpp`. It is silently ignored. The per-branch `const` is what discriminates.
- **`additionalProperties`.** On an object with `properties`, it defaults to **false**: no extra keys (`build_object` only adds an "additional" rule when the keyword is `true` or a schema; L155-165; README L205, L225-230). `{"type":"object"}` with no `properties` accepts **any** object (L166-169). So every operation's argument object must list its properties. `additionalProperties: true` "may produce keys that contain unescaped newlines" (README L206).
- **`required` and ordering.** Property order follows the schema, because `common_json` keeps insertion order (`T/common/json.h` L24). But `_build_object_rule` emits **all required properties first, then the optional ones** (`T/common/json-schema-to-grammar.cpp` L699-794). So `operation` must be listed first **and** be required, or it moves behind the required arguments. Making every property required (nullable when optional) keeps the order exactly as written, and the grammar small, because each optional property adds `-rest` alternation rules.
- **Silently skipped, not errors:** `not`, `if`/`then`/`else`, `dependentSchemas`, `uniqueItems`, `contains`, `patternProperties`, `minProperties`, `$anchor`, `number` bounds, `uri`/`email` formats (README L203-223). The README advises the Python converter to see warnings (L207), but **`examples/json_schema_to_grammar.py` no longer exists at this commit** (not in the tree). The C++ converter only warns, to stderr, for unsupported `pattern`s (converter L390-404, L975-983).
- **Hard errors** (the request fails rather than being silently weakened): unresolved or remote `$ref`, an empty `oneOf`/`anyOf`/`type` array, an unknown `type`, or a non-object schema node (`fail(…)` throws, `T/common/json-schema.cpp` L23, L117-123, L335; converter `check_errors` L975-980).
- **`strict`** in `response_format.json_schema` is not read: only `schema` is (`T/tools/server/server-common.cpp` L1185-1199).
- **The model never sees the schema.** "The JSON schema is only used to constrain the model output and is not injected into the prompt" (README L150-151). The planner prompt must still describe each operation and show an example. `{{response_format}}` already does this for `EditProgramSchema.JsonExample`.

## Grammar size, performance and known failure modes

- **Size.** A union compiles to one rule per alternative (`name-0 | name-1 | …`, `_generate_union_rule`, converter L371-379). With `$ref`s, each alternative is one `ref-defs-…` rule plus one `-kv` rule per property (test "anyOf", test file ~L1168-1200). Ten operations of about five arguments come to roughly 60-150 rules. Rule names cannot collide: `_add_rule` suffixes a clashing name (converter L356-369).
- **The engine is nondeterministic.** `llama_grammar_accept` advances every live stack per character (`T/src/llama-grammar.cpp` ~L1040-1055). After `{"operation": "`, each alternative is one stack (times the few `space` alternatives). The stacks collapse to one as soon as the operation literal diverges. That is why the discriminator should come first in each branch and why `reasoning` should sit **outside** the union: with `reasoning` first inside every branch, all N stacks would stay alive through the whole reasoning string. That is still correct, just N times the grammar work per character.
- **Sampling cost.** The sampler samples first and checks only the chosen token. It runs the full grammar filter only when that token is rejected (`T/common/sampling.cpp` L634-670, as found in the compact-output research). Earlier probes measured 44 tok/s with and without a grammar on qwen-28b (`.scratch/completed/compact-attribution-output/research/01-llama-whitespace-control.md`, Q3).
- **Failure modes found in issues, none of which this schema needs to hit:**
  - [#30056](https://github.com/ggml-org/llama.cpp/issues/30056) (open, 2026-10-06): about 60 **tools** make the grammar fail to parse with `MAX_REPETITION_THRESHOLD` (2000, `T/src/llama-grammar.cpp` L13, L495-496). The cause is the tool-call path's per-argument Aho-Corasick string automaton under `tool-call*` repetition, not the `response_format` JSON path. It is a reason **not** to model operations as `tools`.
  - [#25746](https://github.com/ggml-org/llama.cpp/issues/25746) (closed 2026-09-06, before v0.5.0): `maxLength >= 2000` produced unparseable GBNF. Keep `maxLength`/`maxItems` small or absent. Large `{m,n}` bounds hit the same threshold (`llama-grammar.cpp` L652-657).
  - [#27580](https://github.com/ggml-org/llama.cpp/issues/27580) (open): `\d`, `\w` and `\s` in a `pattern` are copied verbatim into an unparseable grammar, with no error from the converter. Patterns must also be anchored `^…$` (README L212). **Don't put `pattern` on operation arguments.** Validate them in C# after parsing.
  - [#7703](https://github.com/ggml-org/llama.cpp/issues/7703) / README L208: `properties` beside `oneOf` dropped (above).
- **Thinking.** With `response_format`, the Qwen3-Coder handler builds `GEN_PREFIX + (optional <think>…</think> << schema)`. The grammar is not lazy, but the think region is an `until` rule that accepts free text, so thinking is unaffected and the JSON follows `</think>` (`T/common/parsers/qwen3-coder.cpp` L75-87, L179-184; compact-output research Q4). A raw `grammar` gets no such wrapper. With thinking on, it constrains the very first token, and the JSON lands in `reasoning_content` with no thinking (probe p5 in the same research). Hand GBNF therefore needs a `thought "</think>" …` prefix for thinking-on and none for thinking-off (probe p7). Also, `response_format` plus `grammar` on one request silently ignores the grammar (`server-common.cpp` L1179-1203, L1366-1370, L1416-1423).

## What the repo already relies on

- **`EditProgramSchema.JsonSchema`** (`src/Read2Me.Services/BookEdits/EditProgram.cs`) is sent as `response_format` json_schema by `BookEditPlanner` via `LlmCompletionRunner` (thinking on or off per call). It uses only well-supported features: nested objects with every property `required`, nullable `type` arrays, string `enum`s, `enum` with `null`, and `reasoning` first. It is already a **flat, nullable discriminated record**: `transform.kind` plus nullable `pattern`/`replacement`/`template`/`instruction`/`case_mode`. Its known weakness is exactly the flat schema's: nothing in the grammar ties `kind` to the fields it needs, so `EditProgramParser` and the describer have to check that.
- **`BookEditBatchSchema`** (same file) and the attribution/discovery/voice-plan schemas also use `response_format` (`context/llm.md` L5, "Constrained completion").
- **Raw GBNF**: only `RosterGrammar` (`src/Read2Me.Services/Characters/ChapterPass/RosterGrammar.cs`), a plain-text `Name | cue` answer, always sent with `DisableThinking: true` (`ChapterAttributionStep.cs` ~L207). `LlmCompletionRunner` throws `ArgumentException` when both a schema and a grammar are set (L58-59).
- **Request shape**: `OpenAiRequestBuilder.BuildChatBody` sends `response_format: {type: json_schema, json_schema: {name: "response", schema}}`, `chat_template_kwargs.enable_thinking=false` only when thinking is off, and a top-level `grammar` only when set. It sends no `reasoning_format`, so the server default (deepseek extraction) splits the thinking into `reasoning_content`. `LlmCompletionRunner` scans only `content` for the closing brace.
- **`context/llm.md`** names both shapes, "never both on one request". A grammar answer is plain text, and the grammar itself ends it.

## Implications for the contract (ticket 03)

1. The envelope is `{reasoning, plan}`, both required. `plan` is an `anyOf` of `$ref`s, one `$defs` entry per registered operation, plus `unsupported` (`reason`) and `multiple_operations` (operation names as an `enum` of registered names). The registry composes it, so adding an operation adds one `$defs` entry and one `anyOf` item.
2. Each branch: `operation` first, as a `const`. Every property is required, optional ones nullable. Use no `pattern`, no `discriminator` reliance, no siblings beside `$ref`/`anyOf`, and no large `maxLength`/`maxItems`.
3. Add a unit test that the composed schema stays inside this subset: no `properties` beside `anyOf`, `operation` first and required in every branch, distinct `const`s, no `pattern`. The silent drops are the real hazard, and llama.cpp never reports them, so only a test catches them.
4. Parse with `System.Text.Json` polymorphism keyed on `operation`, or switch on it manually.
5. **Follow-up probe** (once a container is up), the same method as the compact-output research: `POST /completion {"prompt":"x","n_predict":1,"json_schema":<composed schema>}` and read `generation_settings.grammar` to confirm the alternation; then run a handful of `EditPlanLiveEvalTests` cases through the union with thinking on and off. This is cheap, and it is the only remaining unknown: model behaviour, not grammar support.
