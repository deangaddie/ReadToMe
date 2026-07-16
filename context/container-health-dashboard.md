# Container health dashboard

Use these terms exactly in code, tests, and discussion.

- **Service Adapter** — the boundary that declares one AI service's display and form metadata, normalizes its readiness, validates its test inputs, and translates its wire protocol into shared progress and result variants. It never owns DOM rendering or the shared run lifecycle.
  _Avoid_: service driver, service plugin, protocol component
- **Service Result** — a completed functional-test value normalized by a Service Adapter as exactly one of four variants: LLM, audio, transcription, or similarity. Protocol chunks and wire-format quirks are not Service Results.
  _Avoid_: raw response, adapter response, test payload
