# Container Health acceptance record

Two passes are recorded here. The first, from 2026-07-16, covered the nine services of the
time. Its rows for the five native TTS containers were removed when ADR 0010 retired them;
they are in git history. The second, from 2026-10-15, covers `read2me-audiocpp`, which now
serves every TTS and voice-design model.

## audio.cpp pass (2026-10-15)

- Tested dashboard source: branch `container-health-audiocpp`, the commit that adds this section
- Host: Windows, NVIDIA GeForce RTX 3070 (8,192 MiB), driver 610.88
- Runtime: Node 24.18.0, npm 11.16.0 (supported)
- Image: `ghcr.io/0xshug0/audio.cpp:full-cuda12`, image ID `5969cc86637c`

`npm run check` passed three consecutive times: typecheck, 74 unit tests, the build, 46
Chromium tests and 28 axe WCAG 2.2 A/AA state tests, with no skips or rule exclusions.

The live pass ran with llama stopped and only `audiocpp` on the GPU (Whisper on the CPU).
Chromium drove the running dashboard through its real proxy. Readiness showed
"Validated response received. Loaded model: qwen3-design." on the overview card, and
"No model loaded." on the detail page right after a container start. Every run used the
text "The quick brown fox jumps over the lazy dog, and the narrator reads on." Clone models
got `test-assets/read2me-acceptance.wav` and its transcript, and design models got the
prompt "A calm middle-aged British narrator with a warm, measured delivery."

Each model was the one not loaded, so every row also exercised the switch phase: the
progress line read "Switching audio.cpp to <model> and generating; the first request after
a switch takes 9–31 s." The Whisper column is the CPU `base.en` transcript of the
downloaded WAV, a machine check of intelligibility rather than a listening test.

| Model | Outcome | Elapsed (incl. switch) | WAV | SHA-256 prefix | Whisper transcript |
| --- | --- | ---: | --- | --- | --- |
| `breeze-q8` | Succeeded | 27.1 s | 257,324 bytes · 24 kHz | `036f0962e558` | exact, with comma |
| `breeze-design` | Succeeded | 25.0 s | 265,004 bytes · 24 kHz | `1c6734bc17b3` | exact, no comma |
| `voxcpm2` | Succeeded | 16.6 s | 445,484 bytes · 48 kHz | `d1020accab66` | exact, with comma |
| `chatterbox` | Succeeded | 9.0 s | 215,084 bytes · 24 kHz | `bc54f3287c34` | exact, with comma |
| `qwen3-base` | Succeeded | 16.6 s | 211,244 bytes · 24 kHz | `47a7a78de6b7` | exact, with comma |
| `qwen3-design` | Succeeded | 13.8 s | 211,244 bytes · 24 kHz | `c47b17a35e20` | exact, with comma |

**Busy.** A direct `qwen3-design` request for about 450 characters held the resident model.
1.5 s later, a dashboard run for `chatterbox` ended at once (0.0 s) as **Busy**: "audio.cpp
is generating with another model; run again when it finishes." It produced no audio player,
and its diagnostic held the service's 503 `server_busy` body. The holding request still
completed with 200.

Not covered: a human listening test of each WAV, and the manual Edge/Firefox matrix below.

## Original nine-service pass (2026-07-16)

- Date: 2026-07-16 (Australia/Perth)
- Tested dashboard source: `561973e2e91c9b11a8ebac4e48ff22d9b4727dc5`
- Acceptance fixture, test, and initial record commit: `6f34b96`
- Host: Windows, NVIDIA GeForce RTX 3070 (8,192 MiB), driver 610.74
- Installed runtime: Node 24.13.0, npm 11.6.2 (**unsupported by this dashboard**)
- Installed browsers: Edge 150.0.4078.65; Firefox 152.0.6

Overall status: **incomplete**. Development checks and live wire-level checks passed, but
the supported-runtime preflight failed: the installed Node and npm are below the required
Node 24.18.0/npm 11.16.0 minimums. The required human Edge/Firefox interaction matrix and
generated-audio audibility/playback observations have also not been performed. This record
does not claim final acceptance.

### Deterministic gate

`npm run check` passed on this host with no Docker service running: 107 unit tests, 51
Chromium tests, the Vite build, and 33 axe WCAG 2.2 A/AA state tests. There were no skips,
focused tests, rule exclusions, or leaked service processes. This is useful development
evidence, but it is not the deterministic acceptance gate because `setup-dashboard.cmd`
correctly rejected Node 24.13.0 before dependency installation. Re-run `npm ci`, explicit
Chromium installation, and `npm run check` under the supported runtime.

The named matrix is owned by these public acceptance seams (as they stand after the
audio.cpp retarget):

| Matrix area | Evidence |
| --- | --- |
| Runtime and real proxy | `runtime.unit.spec.ts`, `proxy.browser.spec.ts` |
| Five Service Adapters and readiness | `service-adapter.unit.spec.ts`, `readiness.unit.spec.ts`, `readiness.browser.spec.ts` |
| Llama SSE and preparation | `llama.unit.spec.ts`, `llama.browser.spec.ts` |
| audio.cpp request shapes, Busy, WAV and audio ownership | `wav.unit.spec.ts`, `audiocpp.unit.spec.ts`, `audiocpp.browser.spec.ts` |
| Whisper parsing and forms | `whisper.unit.spec.ts`, `whisper.browser.spec.ts` |
| Run/poll/race/preferences state | `client-state.unit.spec.ts`, `run-controller.unit.spec.ts`, `detail.browser.spec.ts` |
| WCAG 2.2 A/AA states | `shell.a11y.spec.ts` (no rule exclusions) |
| Committed live fixture contract | `acceptance-assets.unit.spec.ts` |

The fixture is `test-assets/read2me-acceptance.wav`: 9.0145 seconds, 24 kHz mono PCM16,
with its exact BOM-free UTF-8 transcript and CC0 provenance beside it.

### Live wire-level pass

The three CPU services ran together. Before every GPU row, all GPU services were stopped
and Compose showed none running; only the named service was then started. Llama's
`/health` response reached the expected ready payload, and the CPU rows reached Compose
health and functional success. Readiness was not observed through the dashboard UI. The
five native TTS rows from this pass were removed when those containers were retired.

| Service | Image ID (SHA-256 prefix) | Readiness evidence | Functional result | Elapsed | Abort |
| --- | --- | --- | --- | ---: | ---: |
| Whisper | `da064bb0eb99` | Compose healthy | Expected phrase; ordered word timings; duration 9.0145 s | 1.165 s | 0.134 s |
| MiniLM-L6 | `020f81f3ac5b` | Compose healthy | Identical 1.0000001192 > unrelated -0.0851982906 | 0.325 s | 0.041 s |
| MPNet Base v2 | `0dc88a729d72` | Compose healthy | Identical 1.0 > unrelated -0.0052691177 | 0.701 s | 0.044 s |
| Llama | `57f92b5628be` | `/health`: `status=ok` | `gemma-4b` streamed correct Canberra answer through `[DONE]` | 24.587 s | 0.272 s |

Abort timings above are transport evidence only. They do not establish the dashboard's
required immediate cancelled state or absence of later UI mutation; deterministic race
tests cover that behavior, and the live browser matrix below must still confirm it.
Likewise, bounded dashboard diagnostics were not opened for these runs; the wire captures
recorded JSON or byte counts only.

The Whisper base.en model rendered the product name as “Red to Me” but did contain the
locked expected phrase. No transcript was repaired. Generated audio files had valid WAV
structure, but intelligibility, native playback, and downloaded-byte equality were not
claimed without a human browser pass.

## Manual current-stable browser matrix

Record `Pass` or a concrete defect for every cell. A Firefox defect blocks Firefox support.

| Interaction | Edge 150.0.4078.65 | Firefox 152.0.6 |
| --- | --- | --- |
| Overview/detail keyboard navigation and visible focus | Not run | Not run |
| Native and adapter validation; Advanced/diagnostic disclosures | Not run | Not run |
| System/light/dark themes and narrow layout | Not run | Not run |
| Run/Cancel focus and no late mutation | Not run | Not run |
| Status/outcome announcements with accessibility inspector or screen reader | Not run | Not run |
| Generated audio playback and byte-identical download | Not run | Not run |

## Completion conditions

Final acceptance requires a contributor to complete both browser columns against the
live services, listen to each generated WAV, verify playback/download byte identity, and
record per-service dashboard readiness, bounded diagnostics, UI cancellation/no-late-
mutation evidence, and any defects here. The deterministic gate has run under supported
Node/npm (the 2026-10-15 pass), and audio.cpp's generated WAVs have so far been checked by
Whisper only, not by ear. Until the browser columns and the listening check are done, the
overall status stays **incomplete**.
