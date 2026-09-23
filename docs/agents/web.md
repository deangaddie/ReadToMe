# Web front end (Angular) — how it is built and how to extend it

The Angular app in `src/Read2Me.Web` is the second front end beside the Blazor UI ([ADR 0008](../adr/0008-second-front-end-angular-beside-blazor.md)). It is a thin view over the agent API ([api.md](api.md)) and the live hub; it holds no book logic of its own. Vocabulary: [context/web.md](../../context/web.md).

## Run, build, test

| Want | Do |
|---|---|
| Both UIs on one host | `pwsh scripts/build-web.ps1` then `dotnet run --project src/Read2Me.App` → Blazor at `/`, web app at `/app` |
| Web dev loop with live reload | host running on `:5000`, then `npm start` in `src/Read2Me.Web` → `http://localhost:4200/app/` (proxy for `/api`, `/hubs`, `/workspace`, `/openapi`) |
| PR gate for the web project | `npm run check` (lint + typecheck + `api:check` + Vitest + build) or `pwsh scripts/build-web.ps1 -Check` |
| Browser tests for the web app | build the bundle, then `dotnet test src/Read2Me.E2eTests --filter "FullyQualifiedName~Tests.Web"`; without a bundle every web test skips with the command to run |
| API types after a host change | run the host, then `npm run api:types`; `api:check` fails the gate when `schema.d.ts` is stale |

The .NET solution never builds the web project. `dotnet build` and the Blazor E2E tests work with no Node installed; `/app` then serves a "run npm run build" page.

## Layout (`src/Read2Me.Web/src/app`)

| Folder | Holds |
|---|---|
| `app.routes.ts`, `route-meta.ts` | the route table (lazy `loadComponent` per page) and per-route titles/breadcrumbs |
| `shell/` | app bar, nav rail, breadcrumbs, connection dot, theme toggle |
| `pages/` | one folder per page: `projects`, `project` (overview + pipeline), `book` (reader), `cast` (+ `voices`, `voice-rules`), `voice-editor`, `export`, `settings/*`, `styleguide` |
| `ui/` | the `r2m-*` component library; import from `@app/ui`; every component has a story on `/app/styleguide` |
| `api/` | `ApiClient` (ProblemDetails → typed errors, toasts) and one `*Api` service per area; `schema.d.ts` is generated |
| `live/` | `LiveService` (hub client, group membership, snapshot signals), wire types in `live-messages.ts` |
| `activity/` | activity bar, drawer, jobs/streams/services tabs, stream feed |
| `ai-services/` | `AiServicesStore`: catalog, statuses, ops, watchdog log |
| `shared/` | `Preflight` (`ensureReady`) and the preflight sheet host, confirm/prompt helpers |
| `theme/` | `ThemeService`: applies the selected theme as CSS custom properties and `data-theme` on `<html>` |
| `src/styles/_tokens.scss` | design tokens; component SCSS uses tokens, never literal colours |

## Rules the lint enforces

- Selectors are `r2m-*` (library) or `app-*` (feature); an `r2m-*` component sets `host: { class: '<its selector>' }` — that class is what the E2E tests locate by, so it never changes with layout.
- No `@angular/material/chips` or `snack-bar` outside `ui/`: use `r2m-status-chip` / `r2m-speaker-chip` / `ToastService`.
- No `HttpClient` outside `api/`: every request goes through `ApiClient` or a per-area `*Api`, so ProblemDetails mapping and toasts happen once.
- Standalone components, signals, `OnPush`, zoneless. TypeScript `strict` + `noUncheckedIndexedAccess` + `exactOptionalPropertyTypes`.

## Adding a page

1. Create `pages/<area>/<name>-page.ts` as a standalone `OnPush` component. Read through a `*Api` service (add one in `api/` if the area has none) and, for anything that changes over time, subscribe through `LiveService` rather than polling.
2. Add the route to `app.routes.ts` with `loadComponent`, and its title/breadcrumb to `route-meta.ts`. Project-scoped pages sit under `projects/:folder` so `ProjectShell` joins the project group for them.
3. Build the view from `@app/ui` components. A missing building block goes into `ui/` with a story on the styleguide, not into the page.
4. Gate any AI action with `await preflight.ensureReady('<task>')`; the task kinds are `attribution`, `audio`, `voicePrompt`, `discovery`, `voiceDesign`, `transcription`, `bookEdit`.
5. Mutations: send the command, do nothing with the response, and let the receipt (`LiveService.on('receipt')`) drive the reload. Show `ProblemDetails.detail` from the typed error on failure.
6. Tests: a Vitest spec beside the page for state and rendering; a browser test under `src/Read2Me.E2eTests/Tests/Web/<Area>Tests.cs` on `WebE2eTestBase` for the round trip through the host. Locate by `r2m-*` element or host class, BEM classes, `data-action` / `data-entry` / `data-testid` attributes, or ARIA roles and labels. Material element tags (`mat-select`, `mat-option`, `mat-dialog-container`) and the overlay panel (`.mat-mdc-menu-panel`) are acceptable handles; a component's internal `.mat-mdc-*` structure is not.

## The hub contract (`/hubs/live`)

Client → server: `JoinProject(folder)`, `LeaveProject(folder)`, `JoinStream(kind)`, `LeaveStream(kind)`, `GetSnapshot()`. `LiveService` remembers joined groups and re-joins after every reconnect, then re-reads the snapshot.

Server → client, method name = event family, payload carries `kind`:

| Family | Payload | Scope |
|---|---|---|
| `queue` | `{ attribution, audio }` queue snapshots (debounced) | everyone |
| `nodeStatus` | `{ folder, nodes: { [nodeId]: summary }, folderAudioRemaining }` | project group |
| `itemStatus` | `{ folder, paragraphs, items }` per-item status/outcome deltas | project group |
| `receipt` | `BookMutationReceipt` | project group |
| `assembly` | phase / progress / completed / failed / cancelled | everyone |
| `voiceBatch` | started / progress / voiceUpdated / completed / cancelled | project group |
| `watchdog` | recoveryStarted / containerRestarted / serviceHealthy / serviceDown | everyone |
| `serviceStatus` | `{ name, status, op?, ok?, error? }` last observed status per managed service | everyone |
| `preflight` | per-service stages of one preflight run, then `{ kind: done, ok }` | one connection |
| `bookEdit`, `llmTest` | progress / done / failed for one run | one connection |
| `llm`, `audioGen` | stream events (control + batched deltas / phase events) | stream group |
| `throughput` | `ThroughputSnapshot` once a second while a run is active | everyone |
| `settingsChanged` | `{ area }` | everyone |

The C# side lives in `src/Read2Me.App/Live` (`LiveHub`, `LiveRelay`, `LiveMessageMapper`, `LiveMessages`). `live-messages.spec.ts` reads those sources and fails when a family or `kind` string drifts, so add a family on both sides in one change.

## Browser tests

`WebE2eTestBase` (in `src/Read2Me.E2eTests/Infrastructure`) copies the built bundle into the in-proc host's web root and navigates to `/app/...`, waiting for the hub socket. It shares `E2eAppFixture` with the Blazor tests: `FakeAi` (LLM/TTS/whisper replies), `FakeControl` (container statuses, op log), `Encoder` (fake ffmpeg), the seeders (`SeedProjectAsync`, `SeedThreeDialogParagraphProjectAsync`, …) and `WaitForQueueDrainAsync`. State the fixture shares across tests (a shut-down fake service, a theme selection) must be restored in a `finally`.

One file per area under `Tests/Web`, named for what it proves; `LiveUpdateTests` covers two browser contexts on one host and a forced hub disconnect. The parity checklist that says which Blazor function each web route provides is `.scratch/angular-frontend/parity.md`.
