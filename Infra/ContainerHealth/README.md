# Container Health dashboard

This local operator console observes and exercises Read2Me's nine AI services without
starting the Blazor app. It is local-only: Vite binds `127.0.0.1:5173`, exposes only the
nine fixed proxy routes, and keeps inputs, diagnostics, and results in page memory. A
reload discards them; there is no persistence, authentication, LAN hosting, or Docker
control surface.

## Set up and start

Use Node 24 LTS `>=24.18.0 <25` and npm `>=11.16.0 <12`. Playwright Chromium is an explicit
first-time prerequisite and is never downloaded by ordinary startup. From this directory,
install the locked development dependencies and browser, then run the setup gate:

```powershell
npm ci
npx playwright install chromium
setup-dashboard.cmd
```

`setup-dashboard.cmd` validates versions, repeats the frozen `npm ci`, and runs the complete
deterministic gate.

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
