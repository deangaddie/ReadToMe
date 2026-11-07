# Web front end

The UI is the web app in `src/Read2Me.Web`, served by the host at `/app` (`/` redirects there): custom elements rendered with lit-html, signals from `@preact/signals-core` behind a small adapter, `@microsoft/signalr` for the hub, on Bun with no framework ([ADR 0014](../../adr/0014-native-web-front-end-replaces-angular.md)). Those three are the only runtime dependencies; adding one needs an ADR. Bun 1.4.2 (pinned by `packageManager`) is the toolchain; no Node. `src/Read2Me.Web/README.md` has the command list; these pages are is what an agent needs beyond it. Evergreen Chromium and Firefox are both supported, so anything the app uses must ship in both or carry a cheap fallback (a native `<select>` under `@supports (appearance: base-select)`; tooltips are JS-triggered because Firefox has no `interestfor`).

It is a thin view over the agent API ([api/README.md](../api/README.md)) and the live hub, and holds no book logic of its own. Vocabulary: [context/web.md](../../../context/web.md).

Read the part for what you are changing:

- [conventions.md](conventions.md) — before any change to web code: elements, services, router, overlays, mutations, specs, adding a page, gotchas.
- [user-messages.md](user-messages.md) — advice lines, blocked reasons, callouts, permanent-change confirms.
- [layout.md](layout.md) — where things live when the folder name does not say.
- [primitives.md](primitives.md) — the shared building blocks in `ui/`; check it before writing a control.
- [browser-tests.md](browser-tests.md) — before adding or changing an E2E browser test.
- The hub contract and the HTTP API: [../api/README.md](../api/README.md).

## Run, build, test

| Want | Do |
|---|---|
| App on the host | `pwsh scripts/build-web.ps1` then `dotnet run --project src/Read2Me.App` → web app at `/app` (`/` redirects there) |
| Dev loop with HMR | host running on `:5000`, then `bun run dev` in `src/Read2Me.Web` → `http://localhost:4300/app/` (proxies `/api`, `/hubs`, `/workspace`, `/openapi`, `/audio-preview`, `/preview-source`). Restart it after adding a file reached through `@app/*` |
| PR gate for the web project | `bun run check` (`format:check` + lint + typecheck + `icons:check` + `help:check` + `bun test` + build) or `pwsh scripts/build-web.ps1 -Check` |
| One spec | `bun test src/app/core/router.spec.ts` |
| Browser tests | build the bundle, then `dotnet test src/Read2Me.E2eTests --filter-namespace 'Read2Me.E2eTests.Tests.Web*'`; without a bundle every browser test skips with the command to run |
| The same suite in Firefox | `R2M_E2E_BROWSER=firefox dotnet test src/Read2Me.E2eTests` (the gate adds the browser-tests namespace filter above: the API and host tests open no browser, so Chromium covers them) |
| Every CI stage, locally | `pwsh scripts/check.ps1` (`dotnet build`, API contract, web check and build, unit, E2E Chromium, E2E Firefox over the browser tests only; `-SkipInstall` once dependencies are current; a failing E2E stage keeps its traces, screenshots and the `dotnet test` SDK trace in `.artifacts/e2e/<time>-<browser>/`, newest runs only; an E2E stage past `-E2eTimeoutMinutes` (default 10) is killed as a hang, keeping the process table and, with `dotnet-dump` installed, dumps) |
| API types after a host change | `pwsh scripts/api-types.ps1`: writes the host's OpenAPI document to `src/app/api/openapi.json` from the in-proc E2E host (no host to run, no Workspace touched), then `bun run api:types` regenerates `schema.d.ts` from it; fix what `bun run typecheck` points at and commit both files. A new DTO needs nothing in `dtos.ts` or `index.ts`: it arrives as a root type under its C# name, and its XML `<summary>` / `<param>` docs arrive as JSDoc. The gate regenerates both anyway ([ADR 0017](../../adr/0017-openapi-document-is-the-http-contract.md)) |
| A new icon | add the name to the `IconName` union in `ui/icons.ts`, `bun run icons` (network), commit the font and `icons.manifest.json`; `icons:check` fails the gate offline when they drift |
| Drive it by hand | `tools/browse` ([README](../../../tools/browse/README.md)) or the `verify` skill |

The .NET solution never builds the web project: `dotnet build` and the unit tests need no Bun, and without a bundle `/app` serves a "run the build" page and every browser test skips.

## Rules the lint enforces

oxlint's type-aware rules plus `eslint-plugin-lit` on templates; no `fetch` outside `api/` (HTTP goes through `ApiClient`); TypeScript `strict` + `noUncheckedIndexedAccess` + `exactOptionalPropertyTypes`.

## The hub contract (`/hubs/live`)

Methods, families, payloads and scopes are in [../api/live-hub.md](../api/live-hub.md). What the web app adds:

- `LiveService` (`live/`, started once from `main.ts`) remembers joined groups, re-joins them after every reconnect, then re-reads the snapshot. The shell reads its `state` for the connection dot, also on `app-root` as `data-live-state` (the E2E ready hook).
- Every hub record is generated into `schema.d.ts` (ADR 0017), so a drifted field fails `typecheck` once `pwsh scripts/api-types.ps1` regenerates the contract; `live-messages.ts` keeps only what has no wire counterpart.
- A new family needs its `ILiveClient` method on the host, then its name in `LIVE_FAMILIES`, which typecheck holds to the document.
