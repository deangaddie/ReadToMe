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
| `pages/` | `placeholder-page.ts`, what an unported route renders (it names the screen from the route chain and links the same path under `/app`); `pages/project/` holds `r2m-project-shell` (opens the project, holds its hub group, provides `ProjectStore` and `BookStore` to its child routes); `pages/book/` is the reader (`r2m-book-page` over `<r2m-measured-list>` beside `r2m-structure-tree`, the row partials `paragraphRow` / `itemRow`, `BookStore`, `BookEditor` (the one write path, provided by the project shell), the shared `r2m-node-menu` + its `nodeMenuTrigger` partial, the reader's selections (`SelectionStore` for paragraphs, `AudioSelectionStore` for items), `SpeakerAssigner` (every speaker write: assign/clear on an item or paragraph, the bulk assign behind its preview + confirm, create-and-assign) and `AudioGenerator` (every audio enqueue behind the audio preflight, Retry, Dismiss review), all provided by the project shell and reached from a row partial through `use(X, event.currentTarget)`, `r2m-manual-reread-dialog` (`openManualRereadDialog()`) over the pure `manual-reread-form`, `edit-with-ai/` (`r2m-edit-with-ai-dialog` + `openEditWithAiDialog(folder, editor)`, full screen, over the pure `review-model` copied from Angular and `BookEditsApi`), and the pure files `book-tree`, `reader-rows`, `receipt-plan`, `speaker-roster`, `selection`, `bulk-assign`, `node-menu-entries`, `node-menu-commands` copied from Angular); `pages/projects/` is the shelf (`r2m-projects-page` over `ProjectsStore`, the `projectCard` partial per project, `r2m-new-project-dialog` + `openNewProjectDialog()` over the pure `new-project-form` copied from Angular); `pages/project/` also holds the overview (`r2m-overview-page`: the `pipeline()` partial over the pure `pipeline-steps` copied from Angular, beside `r2m-project-details-panel`, both reading the shell's `ProjectStore` through `use(ProjectStore, this)`); `pages/cast/` is the cast page (`r2m-cast-page` at `cast` and `cast/:characterId`: the roster with search and sort, the Add character prompt, `r2m-narrator-banner` (the narrator link over a native `<select>`, emits `narrator-changed`), the `narratorSignpost()` partial for the seed row while a character narrates, and `r2m-character-detail` (rename through `r2m-inline-edit`, alias chips, Merge through `r2m-merge-dialog` + `openMergeDialog()`, Delete behind the destructive confirm, and `r2m-character-lines` over `<r2m-measured-list>` with one expandable `r2m-line-context`); `CastStore` is provided by the page and reached by its children through `use(CastStore, this)`; the pure files `cast-rows`, `alias-collisions`, `merge-options`, `context-paging` are copied from Angular; `CharactersApi` in `api/` holds its reads; the Discover and voice-batch toolbar actions, `?discover=1`, the Voices and Voice rules sections and the store's voice state arrive with the voices screens); then one `pages/<area>/` folder per screen, as in Angular |
| `activity/` | the activity centre (design §5): `ActivityStore` (jobs derived from the hub's snapshot signals, drawer open/tab state, cancel/dismiss, local jobs), `activity-jobs.ts` (pure `deriveJobs`), `stream-events.ts` + `stream-feed.ts` (the reference-counted LLM/audio buffers that join a stream group only while a tab shows it), `r2m-activity-bar` and `r2m-activity-drawer` (+ `r2m-llm-stream-tab`, `r2m-audio-stream-tab`, `r2m-services-tab`; the Jobs tab is the `jobsTab()` partial), `activity.css` |
| `ai-services/` | `AiServicesStore`: the managed-container catalog, hub-fed statuses, in-flight ops and the watchdog log, shared by the drawer's Services tab and (later) the settings page |
| `shared/` | `preflight.ts` (`use(Preflight).ensureReady('<task>')`, the gate before every AI action: plan, then the bottom sheet), `speaker-color.ts`, `debounced.ts` |
| `src/styles/global.css` | design tokens, fonts, the global primitive CSS |
| `src/testing/` | spec helpers: `fake-navigation.ts` (a Navigation API for router-backed specs), `fake-api.ts` (`FakeApi`: an `ApiClient` over routed canned responses, the HttpTestingController stand-in), `fake-timers.ts` (manual `setTimeout`/`setInterval` + `Date.now` for backoff, debounce and ticker specs), `fake-live.ts` (`FakeLive`: the snapshot signals and group membership the shell, project store and activity centre read) |

Pure TypeScript (helpers, stores' pure logic, mappers) is copied from Angular verbatim with its specs; only imports change.

## Conventions

1. **Light DOM + `@scope`.** Component CSS is `@scope (tag) to (child-tag) { … }`; tokens and global primitives live in `global.css`. E2E selectors survive the port: `app-*` tags become `r2m-*` (the root stays `app-root`), and `r2m-` BEM classes and `data-*` attributes keep their names.
2. **CSS is imported as text and adopted** through `adoptStyles()`. Never `import './x.css'`: Bun leaves it unlinked in lazy chunks in production builds, and the dev server hides the bug.
3. **Services.** `use(X)` replaces `inject(X)`; `provide(host, X, instance)` gives a service page scope through DOM ancestry; a class whose constructor takes arguments (`BookStore(projectStore)`) can only be reached that way, since `use()` refuses to build it; `token()` replaces `InjectionToken` (`LIVE_CONNECTION_FACTORY` is kept). Tests use `override()` and `resetServices()`. Stores port with only their imports changed.
4. **Router** (`core/router.ts` + `<r2m-outlet>`): Navigation API + URLPattern, a nested route table with a lazy `load()` per level, signals for params, query and match, breadcrumb titles from the route chain. Links and `Router.navigate()` take app-relative paths (`projects/x/book`) resolved against `<base href>`. `guardUnsaved` cancels the navigation, confirms, then repeats it (`traverseTo` for back/forward); `beforeunload` covers closing the tab. A route with a `title` and no `tag` (`projects`, `settings`) groups its children and takes no outlet level, like an Angular component-less route; an index child (`path: ''`) names the page (tab title, placeholder) but adds no crumb, since it shares its parent's path. A title may be a function of the params that reads a signal (the project crumb reads `ProjectTitles`), and the tab title follows it.
5. **Elements** extend `R2mElement`: `template()` returns lit-html; one effect per element, renders coalesced per microtask; `effect()` and `onDisconnect()` tied to connection; `updated()` is the after-render hook; `await el.rendered()` resolves after the next render. Inputs are getter/setter pairs over a private signal. Outputs are DOM events from `emit()` (`r2m-*` or kebab-case), bubbling only when a parent listens up the tree. Presentational markup without state is a function returning a template, not an element. Register with `define()` and import element modules for their side effect. Don't name a member after an `HTMLElement` member (`remove`, `focus`, `hidden`, `title`, `click`).
6. **Overlays.** Dialogs: `openDialog(el, options?)` over native `<dialog>`, the content emits `r2m-close`; `{ variant: 'sheet' }` docks it to the bottom edge, `{ variant: 'fullscreen' }` fills the viewport (Edit with AI) and `{ closedBy: 'none' }` turns off light dismiss and Escape (MatBottomSheet's `disableClose`). Menus: `popover` + implicit-anchor positioning for a menu with one invoker; a menu shared by many triggers (the node menu, the tooltip) is anchored by an explicit `anchor-name` set on the current trigger. Tooltips: a `data-tooltip` attribute and one shared anchored popover. Select: native `<select>`. The preflight bottom sheet is `openDialog(sheet, { variant: 'sheet', closedBy: 'none' })`.
7. **Virtual scroll is `<r2m-measured-list>`** over the unchanged `HeightIndex`: `key` and `row` functions, then `items`; `scrollToIndex`, `scrollToOffset`, `offsetOf`, `scrollOffset('top'|'bottom')`; the `r2m-top-row` event. Rows are `display: flow-root`. Key rows on ids, never titles. Rows are partials, not elements: the `row` function runs inside the list's own effect, so a signal it reads (the reader's `RowContext`) re-renders the visible rows and nothing else. Every rendered row is watched by a `ResizeObserver`: a row that changes height after its first measure re-measures alone and the top row stays put.
8. **The structure tree is a flat ARIA tree** written by hand (`r2m-structure-tree`: `flattenTree` lists the visible rows, each a `role=treeitem` with `aria-level`/`aria-setsize`/`aria-posinset`/`aria-expanded`, a roving tabindex and the CDK `TreeKeyManager` keys, type-ahead debounced 200 ms); the caller controls expansion (`expanded-change` → `BookStore.setExpanded`). The roving helper (`ui/roving.ts`: `rovingTarget` is the pure key→index rule, `rovingKeydown` moves focus and the single tab stop, `typeAheadTarget` + `TypeAhead` collect letters and find the next match) is shared with the node menu and `tabs()`. **Per-node menus are one shared `r2m-node-menu`** (spec §7, risk 2): a trigger dispatches a bubbling `r2m-node-menu-open`, ancestors on the way (the tree) may fill in `returnTo` and `onAction`, and the menu's parent is its scope. It re-anchors its popover to each open's trigger through an explicit `anchor-name`, so a receipt re-render under it keeps the anchor (lit `repeat` keeps the trigger by id); its keys never reach the tree because the panel lives outside it; Escape and a chosen entry return focus to `returnTo`.
9. **Mutations** keep the Angular rule: send the command, ignore the response, let the `receipt` drive the reload. Book edits (split, insert, rename, merge, delete) all go through the node menu's `commandFor` flows and their top-layer dialogs (`PromptService`, `ConfirmService`) into `BookEditor`; a row holds no editing state, so recycling a row can lose nothing (spec §7, risk 1). AI actions are gated by `use(Preflight).ensureReady('<task>')` (`shared/preflight.ts`): a ready plan resolves `true` at once, otherwise the `r2m-preflight-sheet` opens as a bottom sheet and the run over the hub decides. The reader's Attribute actions (the selection bar, the tree's "Attribute unprocessed") and its audio ones (Generate audio, "Generate audio for this node", Retry, through `AudioGenerator`) are gated; `Tests/Native/PreflightTests` drives the gate through Attribute.
10. **The activity centre is app-wide.** `ActivityStore` and `AiServicesStore` are created once by `use()` and never disposed, like Angular root services; `main.ts` and the shell create them on boot. The drawer is mounted only while open, and a stream tab element acquires its feed on connect and releases it on disconnect, so `stream:llm` / `stream:audio` are joined exactly while visible.
11. **Specs** run under `bun test` on happy-dom. A spec that needs a real browser (dialog focus, popover, anchor positioning, layout) belongs in E2E: happy-dom has no `showPopover`, and `showModal` only flips `open`. A partial is tested by `render(partial(...), host)` from lit-html; an element by creating it, setting properties, appending and `await el.rendered()`.

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
| Field | CSS | `.r2m-field` wrapping a `.r2m-field__label` and a native control, then an optional `.r2m-field__hint` (mat-hint) or `.r2m-field__error` (mat-error, which also reddens the control's border); `.r2m-field--inline` for a label beside the control (the `field()` partial that wires `aria-describedby` arrives with the first form screen) |
| Checkbox | CSS | native `<input type=checkbox class="r2m-checkbox">` |
| Switch | CSS | `<label class="r2m-switch"><input type=checkbox role=switch> Text</label>` (mat-slide-toggle): a track with a knob drawn on the checkbox, `accent` when checked |
| Radio group | CSS | `<fieldset class="r2m-radio-group" role=radiogroup aria-label=…>` of `<label class="r2m-radio"><input type=radio …> Text</label>` (mat-radio-group); the segmented control is the button-toggle look, not this |
| Page header | CSS | `.r2m-page-header` with `__title` and `__subtitle` |
| Dialog | core + CSS | `openDialog(el)` wraps `el` in a `<dialog class="r2m-dialog">` and resolves with the `r2m-close` detail; the content uses `.r2m-dialog__title`, `__content` and `__actions`. Content that must not be dismissed for a while (an upload in flight) calls `setDialogClosedBy(this, 'none')`, then `'any'` |
| Menu | CSS | a `[popover].r2m-menu` anchored to its invoker by CSS anchor positioning; the invoker is a `<button popovertarget=…>`, so the browser handles light dismiss, Escape and the top layer (the speaker chip's menu is one). A plain entry is a `<button class="r2m-menu__item">` with an optional leading `icon()` (`--destructive` for a delete); `<hr class="r2m-menu__divider">` separates groups; a choice among several carries `role="menuitemradio"` + `aria-checked` (the shell's theme menu) |
| Select | CSS | a native `<select>`; under `@supports (appearance: base-select)` Chromium styles the picker, Firefox keeps its own |
| Confirm dialog | element + service | `use(ConfirmService).confirm({ … })` → `boolean`; renders `<r2m-confirm-dialog>` with `.r2m-confirm-dialog__confirm` |
| Text prompt dialog | element + service | `use(PromptService).text({ … })` → `string` or `null`; renders `<r2m-text-prompt-dialog>` with `__input` / `__confirm` |
| Toast | service | `use(ToastService).success/info/warn/error(message)` and `.problem(problemDetails)`; stack is `.r2m-toasts`, each toast `.r2m-toast--<severity>` |
| Tooltip | CSS + one installer | put `data-tooltip="…"` on the element; `installTooltips()` runs once from `main.ts` and owns the single `.r2m-tooltip` popover |
| Measured list | element | `<r2m-measured-list>` — set `key`, `row`, then `items`; emits `r2m-top-row`; `scrollToIndex` / `scrollToOffset` / `offsetOf` / `scrollOffset`; rows are `flow-root`, keyed on ids, re-measured one by one through a `ResizeObserver` |
| Segmented control | CSS | `<fieldset class="r2m-segmented" role="radiogroup" aria-label=…>` of `<label><input type=radio …> Text</label>` (mat-button-toggle-group); locate the group by role and click a label by text |
| Speaker chip | element | `<r2m-speaker-chip>` with `name`, `characterId`, `state` (`named`/`unknown`/`mixed`/`narration`/`narrator-linked`), `roster`, `compact`; with a `roster` it is a `button` that hosts the menu (search focused on open unless the pointer is coarse) and re-emits its events |
| Speaker menu | element | `<r2m-speaker-menu>` with `roster`, `selectedId`, `allowClear`, `allowCreate`; emits bubbling `pick`, `clear`, `create` |
| Outlet | element | `<r2m-outlet>` renders the matched route (core, not `ui/`) |
| Spinner | partial | `spinner(size?, cls?)` — an indeterminate `role=progressbar` ring in `currentColor` (mat-progress-spinner) |
| Progress bar | CSS | a native `<progress max="1" value=…>`; omit `value` for the indeterminate sweep (mat-progress-bar) |
| Structure tree | element | `<r2m-structure-tree>` with `nodes` (`TreeNode[]`), `expandedIds`, `statuses`, `currentChapterId`, `selection` (a `SelectionKind` adds a tri-state `.tree__select` checkbox per node, read from `nodeStates`, plus that selection's menu shortcuts), `locked`; a `role=tree` of flat `role=treeitem` rows (`data-node-id`, `.tree__title`, `.tree__toggle`, `.tree__node--current`); emits `expanded-change`, `select-chapter`, `toggle-node`, `node-action`. Keys: arrows, Home/End, Enter/Space, `*`, type-ahead, Shift+F10 / ContextMenu for the node menu |
| Node menu | element + partial | `nodeMenuTrigger(target, { disabled?, tabIndex? })` renders the `.r2m-node-menu__trigger` icon button (`aria-label="Actions for …"`); one `<r2m-node-menu>` under a common ancestor opens for whichever trigger was clicked, as a `role=menu` popover of `[data-entry]` `role=menuitem` buttons with `.r2m-menu__divider`s and nested pause submenus. Locate entries by `[role='menu'] [data-entry='…']` |
| Tabs | partial | `tabs({ id, tabs, selected, onSelect, label? })` renders a `role=tablist` of `role=tab` buttons (`data-tab`, `aria-selected`, `aria-controls`) with automatic activation over the roving helper; `tabPanel(id, tab, content)` is the matching `role=tabpanel`. Locate a tab by role and name |
| Bottom sheet | core + CSS | `openDialog(el, { variant: 'sheet', closedBy: 'none' })`: the dialog gets `r2m-dialog--sheet` and docks to the bottom edge |
| Full-screen dialog | core + CSS | `openDialog(el, { variant: 'fullscreen' })`: the dialog gets `r2m-dialog--fullscreen` and fills the viewport (MatDialog's `r2m-fullscreen-dialog` panel) |
| Job pill | partial | `jobPill({ job, onOpen, onCancel, tooltip? })` — `.r2m-job-pill` with `__main` (opens the drawer) and `__cancel`; `job` is a `JobView` (`ui/job.ts`: `formatDuration`, `jobSummary`, `JOB_ICONS`) |
| Job card | partial | `jobCard({ job, history?, onCancel, onDismiss })` — `.r2m-job-card` with the numbers `<dl>`, a `<progress>`, an optional sparkline and the Cancel / Dismiss footer |
| Throughput | partial | `throughput(snapshot)` — headline, sparkline and the per-config `.r2m-throughput__table` once the run ended; `sparkline({ values, width?, height?, stroke?, label? })` on its own |
| Docker controls | partial | `dockerControls({ status, busy?, statusOnly?, serviceName?, onStart?, onRestart?, onShutdown?, onRefresh })` — the status chip plus Start / Restart / Shutdown / Refresh icon buttons (by `aria-label`); `SERVICE_STATUS_VIEW` maps `AiServiceStatus` to chip kind + label |
| LLM stream | element | `<r2m-stream-llm>` with `events` (`LlmStreamEvent[]`) and `maxTurns`; folds turns with `ui/llm-turns.ts`, autoscroll pauses on scroll-up (`paused`, "Jump to latest") |
| Audio stream | element | `<r2m-stream-audio>` with `events` (`AudioGenEvent[]`) and `maxCards`; folds cards with `ui/audio-cards.ts`, same autoscroll |
| Preflight sheet | element | `<r2m-preflight-sheet>` with `data: { kind, label, plan }`; owns the plan → running → done/failed phases and the `/run` call, emits `r2m-close` with `true`/`false`. Opened only by `Preflight.ensureReady` |
| Audio player | element | `<r2m-audio-player>` with `src`, `cacheKey` (the `?v=` buster), `label`, `compact`, and `srcB` / `labelA` / `labelB` for the A/B variant; `.r2m-audio-player__toggle` (Play/Pause by `aria-label`), `__scrub`, `__time`, `__error`; emits `playback-ended` and `ab`. `formatClock` is exported. `workspaceUrl(folder, file, version?)` in `api/` builds the `/workspace/…` source |
| Manual reread dialog | element + opener | `openManualRereadDialog()` → `ManualImportRequest` or `null`; `<r2m-manual-reread-dialog>` with `input[name=hasVolumes/hasParts]` `role=switch` switches, a `.r2m-radio-group` per level (`input[name=mode-<level>]`), `input[data-level=…]` prefixes, `[role=alert]` for the host's validation wording, `.manual__submit` / `.manual__cancel` |
| Edit with AI dialog | element + opener | `openEditWithAiDialog(folder, editor)` → `{ applied }` or `null`; `<r2m-edit-with-ai-dialog>` full screen, `.edit[data-phase]` (`instruct`/`plan`/`proposing`/`review`), `[data-testid=instruction]`, the `role=switch` thinking toggles (`[data-testid=plan-thinking]`/`fix-thinking`), `[data-action]` buttons (`analyze`, `generate`, `cancel-proposing`, `select-all`/`-none`, `open-row`, `revert-row`, `retry-row`, `apply`, `start-over`, `close-review`), one `[data-row]` per proposal with its checkbox, `.r2m-status-chip` "Edited", and the open row's `[data-testid=proposed]` textarea and `hint` field. Drafts live in the `ReviewSelection`, never in the row; the AI calls are gated by `Preflight` (`bookEdit`) |
| File drop | element | `<r2m-file-drop>` with `accept` (native syntax: extensions and/or MIME types), `maxBytes`, `multiple`, `label`, `hint`; a dashed drop zone with a "Choose file" button; emits `files` (`File[]`) and `rejected` (`RejectedFile[]`, reason `type` or `size`). The hidden picker keeps `.r2m-file-drop__input` so a browser test can `SetInputFiles` on it |
| Inline edit | element | `<r2m-inline-edit>` with `value`, `placeholder`, `required`, `maxLength`; a `.r2m-inline-edit__display` button that becomes the `.r2m-inline-edit__input` on click (focused and selected). Enter and blur save, Escape cancels; emits `save` only for a changed, valid value, else `cancelled` |
| Key/value | partial | `keyValue(rows, { dense? })` — a `<dl class="r2m-key-value">` of `dt`/`dd` pairs; `mono` rows use the monospace stack, a missing value shows an em dash (`__value--empty`) |
| Project card | partial | `projectCard(project, { onOpen, onDelete, deleting? })` — `.r2m-project-card[data-folder]` with the `__cover` button (workspace image or `placeholderGradient` + `projectInitials`), the `__title` button, the `__more` icon button (`aria-label="Actions for …"`) opening a `role=menu` popover with the `__open` / `__delete` `menuitem`s, the `__author` line, and the `__progress` bar or `__unread` |
| Pipeline | partial | `pipeline({ steps, busyAction?, onAct })` — an `<ol class="r2m-pipeline">` of `.r2m-pipeline__step[data-step]` rows (`--done`, `--next` + `aria-current="step"`), each with the `__marker` (number, icon or check), `__title`, `__chip`, `__detail` and `[data-action]` `__action` buttons; `busyAction` (`step:action`) spins that button and disables all of them. The step rules are the pure `derivePipeline` in `pages/project/pipeline-steps.ts` |
| Narrator banner | element | `<r2m-narrator-banner>` with `narrator` (`NarratorDto`), `rows`, `busy`; unlinked it shows the "Narrated by" native `<select>` of every non-narrator row, linked the name, its ready-voices chip, Change and `.narrator-banner__unlink` (behind the destructive confirm); emits `narrator-changed` with the character id or `null` |
| Character lines | element | `<r2m-character-lines>` with `folder`, `lines` (`CharacterLineDto[]`), `loading`; a `<r2m-measured-list>` of 36 px `.character-lines__row[data-item-id]` rows up to ten tall, each with the `__toggle` (`aria-expanded`, "Show context" / "Hide context") and the `__text` button, which emits `open` with the line; one expanded line renders `<r2m-line-context>` (`[data-action=load-context]`, then `context-previous` / `context-next` up to the host's cap) |
| Merge dialog | element + opener | `openMergeDialog({ folder, merged, rows })` → `{ survivorId, addNameAsAlias }` or `null`; `<r2m-merge-dialog>` with `select[name=survivor]`, `input[name=addNameAsAlias]`, the `.merge__warning` `role=alert` naming the voices lost, `.merge__submit` / `__cancel` |
| New project dialog | element + opener | `openNewProjectDialog()` → the new folder name or `undefined`; `<r2m-new-project-dialog>` with `input[name=bookTitle/title/author]` fields (the project title follows the book title until edited), an `<r2m-file-drop>` for the epub/txt, the `.new-project__file` chips, `.new-project__submit-error` for a host rejection (the form stays open), `.new-project__create` / `__cancel`. While the upload runs the dialog refuses to close |

Not built yet, planned forms: `field()` partial, radio / switch (`role=switch` on a checkbox) / segmented button-toggle / range / `<details>` as CSS.

## E2E: declare `WebApp`

A browser test class states which app it drives: `protected override WebApp WebApp => WebApp.Native;` on `E2eTestBase` (the default is `WebApp.Angular`). `GotoAppAsync("projects/x/book")` then prefixes `/app2`, `AppPath(...)` builds the expected URL, and the class runs in Chromium and Firefox. Native classes live under `Tests/Native`, one file per area, named for what it proves. Locate by `r2m-*` element or BEM class, `data-action` / `data-entry` / `data-testid`, or ARIA roles and labels; there is no Material markup to lean on. Partials keep the Angular element's class as their root (`r2m-job-pill` the tag became `.r2m-job-pill` the class), drawer tabs are `GetByRole(AriaRole.Tab, new() { Name = "LLM" })`, the preflight sheet sits inside `GetByRole(AriaRole.Dialog)`, and a `mat-select` became a native `<select>`: locate it with `GetByRole(AriaRole.Combobox)`, pick with `SelectOptionAsync(new SelectOptionValue { Label = "…" })`, and read its choices from `option:not([disabled])` (a placeholder option is disabled and hidden). A test that needs background work before its screen is native queues it through the agent API (`ActivityCentreTests` posts to `…/attribution/enqueue-paragraphs`).

## Gotchas

- Import CSS as text and use `adoptStyles()`. A plain CSS import breaks in production lazy chunks.
- A class under `Tests/Native` without `protected override WebApp WebApp => WebApp.Native;` runs against `/app`, and most `r2m-*` BEM locators match the Angular DOM too, so it can pass there. Assert on one native-only hook early (`r2m-book-page`, `.r2m-vlist__row`, `.r2m-tooltip`) or check the override when a "native" test behaves oddly.
- Import elements for their side effect; a type-only import never registers the element.
- In a spec, mount an element that contains an `<r2m-outlet>` (the project shell) through a root outlet, as the app does. Appended straight to `body`, its own outlet counts as level 0 and renders the element again inside itself.
- `theme-css.ts` writes the whole §5.1 token set (including `--r2m-accent-container`, `--r2m-secondary*`, `--r2m-surface-highest`) and `global.css` carries a `light-dark()` fallback for each, so a token resolves with or without a host theme. A new token needs both: the mapping and the fallback.
- Element member names collide with `HTMLElement`'s (`remove`, `focus`, `hidden`, `title`, `click`).
- Restart `bun run dev` after adding a file reached through `@app/*`.
- Measured-list rows must be `flow-root`. Key rows on ids, never titles. Never `content-visibility: auto` alone.
- Headless Firefox reports `pointer: none` by default, so a menu that focuses its search only for `(pointer: fine)` never does there. `PlaywrightFixture` sets `ui.primaryPointerCapabilities` / `ui.allPointerCapabilities` to a fine, hovering pointer for Firefox, as a real desktop Firefox reports, so the app behaves as on the user's machine and `ToBeFocused` on the search holds in both browsers.
- `navigate` events for back/forward are cancelable in both browsers; in Chromium that needs user activation.
- Bun 1.4.2 on Windows: `rmSync` throws `EINVAL` for a relative path through `..`, existing or not (resolve it first), and `Bun.build` won't create its `outdir`.
- Computed lists are synchronous, so Enter-before-render races are gone. Don't reintroduce async list derivation.
- Git Bash rewrites an environment value that starts with `/` (`R2M_APP=/app2` becomes a Windows path). Set `MSYS_NO_PATHCONV=1`, use PowerShell, or write `R2M_APP=app2`; the browse helper normalises the slash.
