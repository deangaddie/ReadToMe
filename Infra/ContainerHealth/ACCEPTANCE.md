# Container Health acceptance record

Date: 2026-07-16 (Australia/Perth)  
Tested source commit: `561973e2e91c9b11a8ebac4e48ff22d9b4727dc5` plus the uncommitted
acceptance fixture, test, and documentation in this change  
Host: Windows, NVIDIA GeForce RTX 3070 (8,192 MiB), driver 610.74  
Installed runtime: Node 24.13.0, npm 11.6.2 (**unsupported by this dashboard**)  
Installed browsers: Edge 150.0.4078.65; Firefox 152.0.6

Overall status: **incomplete**. Development checks and live wire-level checks passed, but
the supported-runtime preflight failed: the installed Node and npm are below the required
Node 24.18.0/npm 11.16.0 minimums. The required human Edge/Firefox interaction matrix and
generated-audio audibility/playback observations have also not been performed. This record
does not claim final acceptance.

## Deterministic gate

`npm run check` passed on this host with no Docker service running: 107 unit tests, 51
Chromium tests, the Vite build, and 33 axe WCAG 2.2 A/AA state tests. There were no skips,
focused tests, rule exclusions, or leaked service processes. This is useful development
evidence, but it is not the deterministic acceptance gate because `setup-dashboard.cmd`
correctly rejected Node 24.13.0 before dependency installation. Re-run `npm ci`, explicit
Chromium installation, and `npm run check` under the supported runtime.

The named matrix is owned by these public acceptance seams:

| Matrix area | Evidence |
| --- | --- |
| Runtime and real proxy | `runtime.unit.spec.ts`, `proxy.browser.spec.ts` |
| Nine Service Adapters and readiness | `service-adapter.unit.spec.ts`, `readiness.unit.spec.ts`, `readiness.browser.spec.ts` |
| Llama SSE and preparation | `llama.unit.spec.ts`, `llama.browser.spec.ts` |
| Direct WAV and audio ownership | `wav.unit.spec.ts`, `tts.unit.spec.ts`, `tts.browser.spec.ts` |
| Vox framing and conversion | `vox.unit.spec.ts`, `vox.browser.spec.ts` |
| Whisper parsing and forms | `whisper.unit.spec.ts`, `whisper.browser.spec.ts` |
| Run/poll/race/preferences state | `client-state.unit.spec.ts`, `run-controller.unit.spec.ts`, `detail.browser.spec.ts` |
| WCAG 2.2 A/AA states | `shell.a11y.spec.ts` (no rule exclusions) |
| Committed live fixture contract | `acceptance-assets.unit.spec.ts` |

The fixture is `test-assets/read2me-acceptance.wav`: 9.0145 seconds, 24 kHz mono PCM16,
with its exact BOM-free UTF-8 transcript and CC0 provenance beside it.

## Live wire-level pass

The three CPU services ran together. Before every GPU row, all six GPU services were
stopped and Compose showed none running; only the named service was then started. GPU
`/health` responses reached the expected ready payloads; the CPU rows reached Compose
health and functional success. Readiness was not observed through the dashboard UI.

| Service | Image ID (SHA-256 prefix) | Readiness evidence | Functional result | Elapsed | Abort |
| --- | --- | --- | --- | ---: | ---: |
| Whisper | `da064bb0eb99` | Compose healthy | Expected phrase; ordered word timings; duration 9.0145 s | 1.165 s | 0.134 s |
| MiniLM-L6 | `020f81f3ac5b` | Compose healthy | Identical 1.0000001192 > unrelated -0.0851982906 | 0.325 s | 0.041 s |
| MPNet Base v2 | `0dc88a729d72` | Compose healthy | Identical 1.0 > unrelated -0.0052691177 | 0.701 s | 0.044 s |
| Llama | `57f92b5628be` | `/health`: `status=ok` | `gemma-4b` streamed correct Canberra answer through `[DONE]` | 24.587 s | 0.272 s |
| Chatterbox | `9e32dd04a3b4` | `/health`: `status=ok`, CUDA | Valid 159,438-byte WAV, SHA-256 `78AE…A8C4` | 12.295 s | 0.331 s |
| Chatterbox Turbo | `1d081e7e4b2d` | `/health`: `status=ok`, CUDA | `[chuckle]`; valid 159,438-byte WAV, SHA-256 `01A5…C69` | 9.794 s | 0.331 s |
| Qwen3 Voice Design | `39f0f104aa4c` | `/health`: `status=ok`, CUDA | Valid 272,684-byte WAV, SHA-256 `BB12…BAE` | 10.035 s | 0.321 s |
| Qwen3 Base | `d7de5c61c0de` | `/health`: `status=ok`, CUDA | Exact transcript; valid 172,844-byte WAV, SHA-256 `1DF8…A01B` | 6.545 s | 0.332 s |
| VoxCPM2 | `000d0367d642` | `/health`: loaded | Fresh upload; 49 finite PCM frames + done; 48 kHz | 14.331 s | 0.323 s |

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
mutation evidence, and any defects here. The deterministic gate must also be rerun under
supported Node/npm versions and the final commit recorded. Until then,
`.scratch/container-health-dashboard/issues/impl-08-acceptance.md` must remain open.
