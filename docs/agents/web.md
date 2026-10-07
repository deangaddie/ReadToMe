# Web front end — how it is built and how to extend it

Two apps run side by side while the front end migrates ([ADR 0014](../adr/0014-native-web-front-end-replaces-angular.md)):

- **Angular** (`src/Read2Me.Web`) at `/app` — the UI users see; `/` redirects to it ([ADR 0008](../adr/0008-second-front-end-angular-beside-blazor.md), [ADR 0012](../adr/0012-blazor-ui-removed.md)). **Frozen: bug fixes only.** No new screens, components or dependencies land here; new work goes into the native app.
- **Native** (`src/Read2Me.Native`) at `/app2` — custom elements on Bun, no framework, ported screen by screen. Until a screen is ported its native route renders a placeholder that links to the Angular app. See [Native app (`/app2`) — migration in progress](#native-app-app2--migration-in-progress) below.

Both are thin views over the agent API ([api.md](api.md)) and the live hub; neither holds book logic of its own. Vocabulary: [context/web.md](../../context/web.md). The migration plan, PR sequence and cutover rules are in `.scratch/native-web/spec.md` (local, not tracked).

# Angular app (`/app`) — frozen, bug-fix only

The sections down to "Browser tests" describe the Angular app. They stay accurate until cutover, when they are rewritten for the native app and the Material locator guidance goes.

## Run, build, test

| Want | Do |
|---|---|
| App on the host | `pwsh scripts/build-web.ps1` then `dotnet run --project src/Read2Me.App` → web app at `/app` (`/` redirects there) |
| Web dev loop with live reload | host running on `:5000`, then `npm start` in `src/Read2Me.Web` → `http://localhost:4200/app/` (proxy for `/api`, `/hubs`, `/workspace`, `/openapi`) |
| PR gate for the web project | `npm run check` (lint + typecheck + `api:check` + Vitest + build) or `pwsh scripts/build-web.ps1 -Check` |
| Browser tests for the web app | build the bundle, then `dotnet test src/Read2Me.E2eTests --filter "FullyQualifiedName~Tests.Web"`; without a bundle every web test skips with the command to run |
| API types after a host change | run the host, then `npm run api:types`; `api:check` fails the gate when `schema.d.ts` is stale |

The .NET solution never builds the web project. `dotnet build` and the unit tests work with no Node installed; `/app` then serves a "run npm run build" page and every browser test skips.

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
6. Tests: a Vitest spec beside the page for state and rendering; a browser test under `src/Read2Me.E2eTests/Tests/Web/<Area>Tests.cs` on `E2eTestBase` for the round trip through the host. Locate by `r2m-*` element or host class, BEM classes, `data-action` / `data-entry` / `data-testid` attributes, or ARIA roles and labels. Material element tags (`mat-select`, `mat-option`, `mat-dialog-container`) and the overlay panel (`.mat-mdc-menu-panel`) are acceptable handles; a component's internal `.mat-mdc-*` structure is not.

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
| `voiceBatch` | started / progress / voiceUpdated (fresh audio carries `referenceSeconds` / `referenceWarning`) / completed / cancelled | project group |
| `watchdog` | recoveryStarted / containerRestarted / serviceHealthy / serviceDown | everyone |
| `serviceStatus` | `{ name, status, op?, ok?, error? }` last observed status per managed service | everyone |
| `preflight` | per-service stages of one preflight run, then `{ kind: done, ok }` | one connection |
| `bookEdit`, `llmTest` | progress / done / failed for one run | one connection |
| `llm`, `audioGen` | stream events (control + batched deltas / phase events; `audioGen` `modelLoading` carries an all-zero (`Guid.Empty`) id — the TTS gate sits below any item) | stream group |
| `throughput` | `ThroughputSnapshot` once a second while a run is active | everyone |
| `settingsChanged` | `{ area }` | everyone |

A voice's Reference Limit state (`context/voice-rules.md`) reaches the page from `VoiceDto` or a live `voiceUpdated`; the voice card and the voice editor show `referenceWarning` as a warn chip labelled with `referenceSeconds`, and the voice-design sample-text card counts against the API's `maxLength`.

The C# side lives in `src/Read2Me.App/Live` (`LiveHub`, `LiveRelay`, `LiveMessageMapper`, `LiveMessages`). `live-messages.spec.ts` reads those sources and fails when a family or `kind` string drifts, so add a family on both sides in one change.

## Browser tests

`E2eTestBase` (in `src/Read2Me.E2eTests/Infrastructure`) copies the built bundle of the app the class declares (`WebApp`: Angular at `/app` by default, or `WebApp.Native` at `/app2`) into the in-proc host's web root; `GotoAppAsync` takes an app-relative path (`"projects/x/book"`), adds the prefix, and waits for the live hub socket to open. `R2M_E2E_BROWSER=chromium|firefox` picks the browser; Angular classes skip under Firefox, native classes (`Tests/Native`) run in both. It runs on the collection-shared `E2eAppFixture`: `FakeAi` (LLM/TTS/whisper replies), `FakeControl` (container statuses, op log), `Encoder` (fake ffmpeg), the seeders (`SeedProjectAsync`, `SeedThreeDialogParagraphProjectAsync`, …) and `WaitForQueueDrainAsync`. State the fixture shares across tests (a shut-down fake service, a theme selection) must be restored in a `finally`.

One file per area under `Tests/Web`, named for what it proves; `LiveUpdateTests` covers two browser contexts on one host and a forced hub disconnect.

# Native app (`/app2`) — migration in progress

`src/Read2Me.Native`: custom elements rendered with lit-html, signals from `@preact/signals-core` behind an Angular-shaped adapter, `@microsoft/signalr` for the hub. Those three are the only runtime dependencies; adding one needs an ADR. Bun 1.4.2 (pinned by `packageManager`) is the toolchain; no Node. Its `README.md` has the command list; this section is what an agent needs beyond it. Evergreen Chromium and Firefox are both supported, so anything the app uses must ship in both or carry a cheap fallback (a native `<select>` under `@supports (appearance: base-select)`; tooltips are JS-triggered because Firefox has no `interestfor`).

## Run, build, test

| Want | Do |
|---|---|
| App on the host | `pwsh scripts/build-native.ps1` then `dotnet run --project src/Read2Me.App` → native app at `/app2` (`/` still redirects to Angular) |
| Dev loop with HMR | host running on `:5000`, then `bun run dev` in `src/Read2Me.Native` → `http://localhost:4300/app2/` (proxies `/api`, `/hubs`, `/workspace`, `/openapi`, `/audio-preview`, `/preview-source`). Restart it after adding a file reached through `@app/*` |
| PR gate for the native project | `bun run check` (lint + typecheck + `api:check` + `icons:check` + `bun test` + build) or `pwsh scripts/build-native.ps1 -Check` |
| One spec | `bun test src/app/core/router.spec.ts` |
| Browser tests for the native app | build the bundle, then `dotnet test src/Read2Me.E2eTests --filter "FullyQualifiedName~Tests.Native"`; without a bundle every native test skips with the command to run |
| The same suite in Firefox | `R2M_E2E_BROWSER=firefox dotnet test src/Read2Me.E2eTests` — native classes run, Angular classes skip |
| Every CI stage, locally | `pwsh scripts/check.ps1` (native check, Angular build, `dotnet build`, unit, E2E Chromium, E2E Firefox; `-SkipInstall` once dependencies are current) |
| API types after a host change | run the host, then `bun run api:types`; `api:check` fails the gate when `schema.d.ts` is stale (skips with a warning when the host is down) |
| A new icon | add the name to the `IconName` union in `ui/icons.ts`, `bun run icons` (network), commit the font and `icons.manifest.json`; `icons:check` fails the gate offline when they drift |
| Drive it by hand | `R2M_APP=/app2` with `tools/browse` ([README](../../tools/browse/README.md)) or the `verify` skill |

As with Angular, the .NET solution never builds it: `dotnet build` and the unit tests need no Bun, and without a bundle `/app2` serves a "run the build" page.

## Layout (`src/Read2Me.Native/src/app`)

| Folder | Holds |
|---|---|
| `core/` | the "framework": `signals.ts` (`signal`, `computed`, `effect`, `untracked`, `batch`), `element.ts` (`R2mElement` + `define()`), `services.ts` (`use`, `provide`, `token`, `override`, `resetServices`), `router.ts` + `outlet.ts` (`<r2m-outlet>`), `dialog.ts` (`openDialog`), `styles.ts` (`adoptStyles`), `emitter.ts` |
| `ui/` | shared elements, partials and the global-primitive hooks — the catalogue below; `icons.ts` holds the `IconName` union |
| `api/` | `ApiClient` (the only place that calls `fetch`; non-2xx → a typed `ApiError` with the ProblemDetails `status`/`title`/`detail`, which callers hand to `ToastService.problem()`), `dtos.ts`, `book-commands.ts`, `api-error.ts`, the generated `schema.d.ts`; per-area facades arrive with their screens |
| `live/` | `LiveService` (Subject → `Emitter`), `live-connection.ts` (`LIVE_CONNECTION_FACTORY`), `live-messages.ts`, `live-state.ts`, `hub-events.ts`; `live-messages.spec.ts` reads the C# hub sources, as the Angular one does. Started once from `main.ts`; the shell reads `state` for its connection dot |
| `app.routes.ts`, `route-meta.ts` | the route table (every Angular route; unported leaves render the placeholder) and `shellContext()`, which reads the section, page title and folder off the matched chain — what Angular carried as route `data` |
| `shell/` | `app-root` (`shell.ts`): the app bar with breadcrumbs from the route chain, the live dot and the theme quick menu, the nav rail, the main outlet; `shell-nav.ts` (context + global items, `isActive`), `rail-state.ts` (localStorage), `project-titles.ts` (the title the project crumb shows once the store loads it) |
| `theme/` | `theme-css.ts` (pure `AppTheme` → `--r2m-*` mapping, spec §5.1) and `ThemeService` (loads the shared selection on boot, applies `<style id="r2m-theme">` + `data-theme` on `<html>`, follows the OS preference live) |
| `pages/` | `placeholder-page.ts`, what an unported route renders (it names the screen from the route chain and links the same path under `/app`); `pages/project/` holds `r2m-project-shell` (opens the project, holds its hub group, provides `ProjectStore` to its child routes); then one `pages/<area>/` folder per screen, as in Angular |
| `activity/`, `ai-services/`, `shared/` | as in Angular, ported with the screens that need them (`shared/speaker-color.ts` and `shared/debounced.ts` are there already) |
| `src/styles/global.css` | design tokens, fonts, the global primitive CSS |
| `src/testing/` | spec helpers: `fake-navigation.ts` (a Navigation API for router-backed specs), `fake-api.ts` (`FakeApi`: an `ApiClient` over routed canned responses, the HttpTestingController stand-in), `fake-timers.ts` (manual `setTimeout` for backoff and debounce specs) |

Pure TypeScript (helpers, stores' pure logic, mappers) is copied from Angular verbatim with its specs; only imports change.

## Conventions

1. **Light DOM + `@scope`.** Component CSS is `@scope (tag) to (child-tag) { … }`; tokens and global primitives live in `global.css`. E2E selectors survive the port: `app-*` tags become `r2m-*` (the root stays `app-root`), and `r2m-` BEM classes and `data-*` attributes keep their names.
2. **CSS is imported as text and adopted** through `adoptStyles()`. Never `import './x.css'`: Bun leaves it unlinked in lazy chunks in production builds, and the dev server hides the bug.
3. **Services.** `use(X)` replaces `inject(X)`; `provide(host, X, instance)` gives a service page scope through DOM ancestry; `token()` replaces `InjectionToken` (`LIVE_CONNECTION_FACTORY` is kept). Tests use `override()` and `resetServices()`. Stores port with only their imports changed.
4. **Router** (`core/router.ts` + `<r2m-outlet>`): Navigation API + URLPattern, a nested route table with a lazy `load()` per level, signals for params, query and match, breadcrumb titles from the route chain. Links and `Router.navigate()` take app-relative paths (`projects/x/book`) resolved against `<base href>`. `guardUnsaved` cancels the navigation, confirms, then repeats it (`traverseTo` for back/forward); `beforeunload` covers closing the tab. A route with a `title` and no `tag` (`projects`, `settings`) groups its children and takes no outlet level, like an Angular component-less route; an index child (`path: ''`) names the page (tab title, placeholder) but adds no crumb, since it shares its parent's path. A title may be a function of the params that reads a signal (the project crumb reads `ProjectTitles`), and the tab title follows it.
5. **Elements** extend `R2mElement`: `template()` returns lit-html; one effect per element, renders coalesced per microtask; `effect()` and `onDisconnect()` tied to connection; `updated()` is the after-render hook; `await el.rendered()` resolves after the next render. Inputs are getter/setter pairs over a private signal. Outputs are DOM events from `emit()` (`r2m-*` or kebab-case), bubbling only when a parent listens up the tree. Presentational markup without state is a function returning a template, not an element. Register with `define()` and import element modules for their side effect. Don't name a member after an `HTMLElement` member (`remove`, `focus`, `hidden`, `title`, `click`).
6. **Overlays.** Dialogs: `openDialog(el)` over native `<dialog>`, the content emits `r2m-close`. Menus: `popover` + implicit-anchor positioning. Tooltips: a `data-tooltip` attribute and one shared anchored popover. Select: native `<select>`. The preflight bottom sheet: `openDialog()` with a bottom-docked dialog class.
7. **Virtual scroll is `<r2m-measured-list>`** over the unchanged `HeightIndex`: `key` and `row` functions, then `items`; `scrollToIndex`, `scrollToOffset`, `offsetOf`, `scrollOffset('top'|'bottom')`; the `r2m-top-row` event. Rows are `display: flow-root`. Key rows on ids, never titles.
8. **The structure tree is a flat ARIA tree** written by hand (`aria-level`/`aria-setsize`/`aria-posinset`/`aria-expanded`, roving tabindex, CDK `TreeKeyManager` keys, type-ahead debounced 200 ms); the caller controls expansion. The roving/type-ahead helper is shared with the menu and `tabs()`.
9. **Mutations** keep the Angular rule: send the command, ignore the response, let the `receipt` drive the reload. AI actions are gated by `preflight.ensureReady('<task>')`.
10. **Specs** run under `bun test` on happy-dom. A spec that needs a real browser (dialog focus, popover, anchor positioning, layout) belongs in E2E: happy-dom has no `showPopover`, and `showModal` only flips `open`.

## Rules the lint enforces

oxlint's type-aware rules plus `eslint-plugin-lit` on templates; no `fetch` outside `api/` (HTTP goes through `ApiClient`); TypeScript `strict` + `noUncheckedIndexedAccess` + `exactOptionalPropertyTypes`. The Angular-only rules (`r2m/host-class`, the Material import bans) have no counterpart.

## Primitive catalogue

The rule for a new primitive: CSS on a native control by default; a partial (function returning a template) only when it wires ARIA or ids; an element only when it owns state or lifecycle. Each screen PR adds a row for every primitive it introduces. The look is M3-flavoured: pill buttons, outlined fields with the label stacked above (not floating), density −1, no ripple.

| Primitive | Form | Usage |
|---|---|---|
| Icon | partial | `icon(name, cls?)` — `name` is an `IconName`; the committed font is a subset of exactly those names |
| Status chip | partial | `statusChip({ status, label, icon?, tooltip?, compact? })` — `status` is `ok`/`warn`/`error`/`info`/`busy`/`neutral`; colour + icon + text, never colour alone |
| Empty state | partial | `emptyState({ icon, headline, hint?, compact? }, action?)` |
| Count badge | partial | `countBadge(kind, count, tooltip, showZero?)` — `kind` is `attribution`/`audio`/`review`; renders nothing at zero unless asked |
| Button | CSS | `<button class="r2m-button">` is the text variant; add `r2m-button--filled`, `--stroked` or `--danger` |
| Icon button | CSS | `<button class="r2m-icon-button">` or an `<a>` with the same class: a 40 px round hit area around one `icon()`, colour inherited from its bar; give it `aria-label` and `data-tooltip` (the mat-icon-button) |
| Field | CSS | `.r2m-field` wrapping a `.r2m-field__label` and a native control; `.r2m-field--inline` for a label beside the control (the `field()` partial that wires `aria-describedby` arrives with the first form screen) |
| Checkbox | CSS | native `<input type=checkbox class="r2m-checkbox">` |
| Page header | CSS | `.r2m-page-header` with `__title` and `__subtitle` |
| Dialog | core + CSS | `openDialog(el)` wraps `el` in a `<dialog class="r2m-dialog">` and resolves with the `r2m-close` detail; the content uses `.r2m-dialog__title`, `__content` and `__actions` |
| Menu | CSS | a `[popover].r2m-menu` anchored to its invoker by CSS anchor positioning; the invoker is a `<button popovertarget=…>`, so the browser handles light dismiss, Escape and the top layer (the speaker chip's menu is one). A plain entry is a `<button class="r2m-menu__item">` with an optional leading `icon()`; a choice among several carries `role="menuitemradio"` + `aria-checked` (the shell's theme menu) |
| Select | CSS | a native `<select>`; under `@supports (appearance: base-select)` Chromium styles the picker, Firefox keeps its own |
| Confirm dialog | element + service | `use(ConfirmService).confirm({ … })` → `boolean`; renders `<r2m-confirm-dialog>` with `.r2m-confirm-dialog__confirm` |
| Text prompt dialog | element + service | `use(PromptService).text({ … })` → `string` or `null`; renders `<r2m-text-prompt-dialog>` with `__input` / `__confirm` |
| Toast | service | `use(ToastService).success/info/warn/error(message)` and `.problem(problemDetails)`; stack is `.r2m-toasts`, each toast `.r2m-toast--<severity>` |
| Tooltip | CSS + one installer | put `data-tooltip="…"` on the element; `installTooltips()` runs once from `main.ts` and owns the single `.r2m-tooltip` popover |
| Measured list | element | `<r2m-measured-list>` — set `key`, `row`, then `items`; emits `r2m-top-row` |
| Speaker chip | element | `<r2m-speaker-chip>` with `name`, `characterId`, `state` (`named`/`unknown`/`mixed`/`narration`/`narrator-linked`), `roster`, `compact`; hosts the menu and re-emits its events |
| Speaker menu | element | `<r2m-speaker-menu>` with `roster`, `selectedId`, `allowClear`, `allowCreate`; emits bubbling `pick`, `clear`, `create` |
| Outlet | element | `<r2m-outlet>` renders the matched route (core, not `ui/`) |

Not built yet, planned forms: `field()` partial, `spinner()` partial, `tabs()` partial (roving `role=tablist`), radio / switch (`role=switch` on a checkbox) / segmented button-toggle / range / `<progress>` / `<details>` as CSS, the bottom sheet as a dialog class.

## E2E: declare `WebApp`

A browser test class states which app it drives: `protected override WebApp WebApp => WebApp.Native;` on `E2eTestBase` (the default is `WebApp.Angular`). `GotoAppAsync("projects/x/book")` then prefixes `/app2`, `AppPath(...)` builds the expected URL, and the class runs in Chromium and Firefox. Native classes live under `Tests/Native`, one file per area, named for what it proves. Locate by `r2m-*` element or BEM class, `data-action` / `data-entry` / `data-testid`, or ARIA roles and labels; there is no Material markup to lean on.

## Gotchas

- Import CSS as text and use `adoptStyles()`. A plain CSS import breaks in production lazy chunks.
- Import elements for their side effect; a type-only import never registers the element.
- In a spec, mount an element that contains an `<r2m-outlet>` (the project shell) through a root outlet, as the app does. Appended straight to `body`, its own outlet counts as level 0 and renders the element again inside itself.
- `theme-css.ts` writes the whole §5.1 token set (including `--r2m-accent-container`, `--r2m-secondary*`, `--r2m-surface-highest`) and `global.css` carries a `light-dark()` fallback for each, so a token resolves with or without a host theme. A new token needs both: the mapping and the fallback.
- Element member names collide with `HTMLElement`'s (`remove`, `focus`, `hidden`, `title`, `click`).
- Restart `bun run dev` after adding a file reached through `@app/*`.
- Measured-list rows must be `flow-root`. Key rows on ids, never titles. Never `content-visibility: auto` alone.
- Headless Firefox reports `pointer: none`, so menus don't autofocus there (by design, the same as Angular).
- `navigate` events for back/forward are cancelable in both browsers; in Chromium that needs user activation.
- Bun 1.4.2 on Windows: `rmSync` throws `EINVAL` for a relative path through `..`, existing or not (resolve it first), and `Bun.build` won't create its `outdir`.
- Computed lists are synchronous, so Enter-before-render races are gone. Don't reintroduce async list derivation.
- Git Bash rewrites an environment value that starts with `/` (`R2M_APP=/app2` becomes a Windows path). Set `MSYS_NO_PATHCONV=1`, use PowerShell, or write `R2M_APP=app2`; the browse helper normalises the slash.
