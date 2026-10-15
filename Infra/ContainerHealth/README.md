# Container Health dashboard

This local operator console observes and exercises Read2Me's five AI services — `llama`
(:8080), `audiocpp` (:8004), `whisper` (:9000), `minilm-l6` (:8200) and `mpnet-base-v2`
(:8201) — without starting the Read2Me host. It is local-only: Vite binds `127.0.0.1:5173`,
exposes only the five fixed proxy routes, and keeps inputs, diagnostics, and results in page
memory. A reload discards them; there is no persistence, authentication, LAN hosting, or
Docker control surface.

`audiocpp` is one service that serves every TTS and voice-design model (ADR 0010). Its card
names the loaded model, and its workbench has a **Model** select prepared from
`GET /v1/models`, which is cross-checked against the six default ids of
`Infra/audiocpp/server.json`. The form shows only the fields the selected model takes, and
each request uses that model family's shape from [Infra/README.md](../README.md), "audio.cpp
(TTS)", with the app's default knobs fixed and listed in the input summary. Running a model
that is not loaded shows a model-switch phase (9–31 s). A 503 `server_busy` ends the run
as **Busy**, a neutral outcome rather than a failure, and there is no automatic retry.

## Set up and start

Use Node 24 LTS `>=24.18.0 <25` and npm `>=11.16.0 <12`. Playwright Chromium is an explicit
first-time prerequisite and is never downloaded by ordinary startup. From this directory,
install the locked development dependencies and browser, then run the setup gate:

```powershell
npm ci
npx playwright install chromium
setup-dashboard.cmd
```

`setup-dashboard.cmd` validates versions, repeats the frozen `npm ci`, installs Playwright
Chromium, and runs the complete deterministic gate.

Start the long-running console with:

```powershell
start-dashboard.cmd
```

Startup never downloads or repairs dependencies. It fails visibly when prerequisites are
missing or port 5173 is occupied.

## Optional target overrides

Copy `.env.example` to ignored `.env.local` and change only the required
`CHD_TARGET_<SERVICE>` origins. Each value must be a complete `http:` or `https:` origin
without credentials, a path, query, or fragment. Targets are server-side configuration;
the browser cannot create an arbitrary proxy.

## Verify

The CPU-only deterministic gate needs no Docker, models, GPU, service, or network after
the locked packages and Chromium are installed:

```powershell
npm ci
npx playwright install chromium
npm run check
```

`npm run check` runs type checking, unit tests, the production build, Chromium flows, and
axe accessibility tests in that order. Unexpected skipped/focused tests fail the gate.

The hardware/model-dependent live sequence and the manual Edge/Firefox matrix are recorded
in [ACCEPTANCE.md](ACCEPTANCE.md). Follow the service scheduling and start/stop guidance in
[Infra/README.md](../README.md). Missing hardware, models, audibility checks, or manual
browser evidence leaves live acceptance incomplete rather than weakening the gate.
