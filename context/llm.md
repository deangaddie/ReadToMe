# LLM infrastructure

Use these terms exactly in code, tests, and discussion.

- **Constrained completion** — an LLM request whose output is constrained to a JSON schema (llama.cpp compiles the schema to a grammar, so the model cannot emit anything but the schema).
- **Completion Runner** — the single module (`ILlmCompletionRunner`) that runs a constrained completion end to end: streams via `ILlmClient`, publishes the Audio Gen Stream-style broadcast events, stops at the completion scanner, records stream metrics, reports health streaks, and maps failure to an outcome. Every LLM-calling feature (attribution, discovery, book edits, voice prompts) goes through it — never through `ILlmClient` directly.
  _Avoid_: "LLM helper", "LLM wrapper", calling `StreamChatAsync` from feature code
- **Completion scanner stop** — breaking the stream the moment the answer JSON object/array closes (`JsonCompletionScanner`); disposing the stream cancels the request if the model keeps generating.
- **Run outcome** — the Completion Runner's four-way result: `Completed`, `ParseFailed`, `Failed`, or `ServiceUnavailable` (managed docker service failure — triggers requeue, orthogonal to answer quality). A genuine cancel is not an outcome; it throws through.
- **Token budget** — the output-token floor a request needs (`AttributionTokenBudget`). A segment answer copies every answered paragraph back verbatim, so the budget must grow with the passage; a fixed config `max_tokens` truncates a large batch, and a truncated answer is unparseable. Raises the config's `max_tokens` for that request only, never lowers it, and never caps a config that sets none.
- **Health streak** — consecutive-failure tracking per managed AI service (`IAiServiceReporter`); a completed stream clears the streak, a failure may escalate to service-unavailable handling.
