# Read2Me web

The ReadToMe UI: custom elements on Bun, with no framework (ADR 0014). The host serves the
production bundle at `/app`. It is **not** part of `Read2Me.slnx`; `dotnet build` never touches it.

## Prerequisites

- Bun 1.4.2, pinned by `packageManager` in `package.json`. No Node is needed.
- A running ReadToMe host on `http://localhost:5000` for anything that talks to the API
  (`dotnet run --project src/Read2Me.App` from the repo root).

## Commands

```bash
bun install --frozen-lockfile   # install exactly what bun.lock says
bun run dev          # dev server with HMR → http://localhost:4300/app/ (proxies /api, /hubs, /workspace, /openapi, /audio-preview, /preview-source to :5000)
bun run build        # production build → ../Read2Me.App/wwwroot/app/ (git-ignored)
bun run check        # format:check + lint + typecheck + api:check + icons:check + help:check + tests + build — must be green before a PR
bun run lint         # oxlint, type-aware, with eslint-plugin-lit
bun run typecheck    # tsc --noEmit
bun test             # bun test over happy-dom; `bun test src/app/core/router.spec.ts` for one file
bun run format       # oxfmt, rewrites files in place
bun run format:check # oxfmt --check: fail on any unformatted file (first step of check)
bun run api:types    # regenerate src/app/api/schema.d.ts from the host's /openapi/v1.json
bun run api:check    # fail if schema.d.ts is stale against the running host (skips with a warning when the host is down)
bun run icons        # rebuild the subset icon font and icons.manifest.json from the IconName union (needs the network)
bun run icons:check  # offline: fail if icons.manifest.json differs from the IconName union
bun run help         # render docs/guide/<step>.md into src/app/help/help.generated.ts (committed)
bun run help:check   # fail if help.generated.ts is stale against docs/guide, or a page breaks the lint
```

`tsc` is TypeScript 7, which has no JavaScript compiler API. openapi-typescript builds its
output with the TypeScript 6 API, so the openapi-typescript patch in `patches/` points its
`typescript` imports at `@typescript/typescript6`. `bun install` applies the patch. When you upgrade
openapi-typescript, run `bun patch openapi-typescript` again, or drop the patch and the shim once
openapi-typescript supports TypeScript 7.

Restart `bun run dev` after adding a file that is reached through the `@app/*` alias: the dev
bundler does not see it until then.

## Layout

- `base.ts` — `BASE`, the path the app is served under. `build.ts` and `dev.ts` both read it, and
  `base.spec.ts` holds `index.html`'s `<base href>` to it.
- `src/app/core/` — the "framework": the signals adapter, the `R2mElement` base class and
  `define()`, `use`/`provide`/`token` services, the Navigation-API router and `<r2m-outlet>`,
  `openDialog`, `adoptStyles`, `Emitter`.
- `src/app/ui/` — shared elements and partials: the measured list, speaker chip and menu, dialogs,
  toast, tooltip, `icon()`, `statusChip()`, `emptyState()`, `countBadge()`, `spinner()`. `icons.ts` holds the
  `IconName` union: `icon()` takes nothing else, and the committed icon font
  (`src/assets/fonts/MaterialSymbolsRounded.woff2`) is a subset holding only those names. To use a
  new icon, add its name to the union, run `bun run icons`, and commit the font and
  `icons.manifest.json` with it.
- `src/app/api/` — `ApiClient` (the only place that calls `fetch`), the wire shapes in `dtos.ts`,
  the `BookCommand` union and the generated `schema.d.ts` (checked in).
- `src/app/live/` — the `/hubs/live` client. `live-messages.spec.ts` reads the C# hub sources and
  fails when a family or `kind` string drifts.
- `src/app/app.routes.ts`, `src/app/route-meta.ts` — the route table and per-route metadata.
- `src/app/shell/`, `src/app/pages/` — the shell frame and the screens; an unknown path renders
  `pages/not-found-page.ts`.
- `src/app/activity/` — the activity centre (bar pill, drawer, stream feed).
- `src/app/ai-services/` — the store behind the AI services page and its docker controls.
- `src/app/shared/` — non-element helpers shared across pages: config forms, preflight, the
  settings schema, speaker colours, the unsaved-changes guard.
- `src/app/theme/` — the theme service and theme CSS.
- `src/styles/global.css` — design tokens, fonts and the global primitive CSS.
- `src/testing/` — test helpers: fake Navigation API, API client, live hub and timers.
- `src/app/help/` — `helpText(step, { title? })`, the `r2m-help` partial, over `help.generated.ts`, which
  `bun run help` renders from the repo's `docs/guide/<step>.md`. See "Help text" below.
- `scripts/` — `check-api-types.ts` (`api:check`), `icons.ts` / `icon-manifest.ts`
  (`icons`, `icons:check`) and `help.ts` / `help-render.ts` (`help`, `help:check`);
  `icons.manifest.json` sits at the project root.

## Help text

Each Pipeline step's help is one markdown file, `docs/guide/<step>.md` at the repo root, one per
`PipelineStepId` (an optional `docs/guide/README.md` is ignored). The first line is `# <Step name>`:
it becomes the title and is left out of the body, whose headings start at `##` (rendered one level
down, under the partial's `<h2>` title). The shape is `# Name`, one "what this step is for"
paragraph, `## How`, `## Done when`, about 150–300 words. No front matter, no images, no raw HTML
(it is escaped), no links into the app: name the control in prose. Links are absolute `http(s)://`
URLs and open in a new tab.

`bun run help` renders the nine files into `src/app/help/help.generated.ts`, which is committed;
`bun run help:check` (part of `bun run check`) fails when the module is stale or a page breaks the
lint (`javascript:`/`data:`/relative/`/app` links, `![` images, a missing `# Title`). The authoring
loop runs from the **repo root**, because Bun's watcher only sees files under its working directory
and `docs/guide` sits outside this package:

```bash
bun --watch src/Read2Me.Web/scripts/help.ts   # regenerates the module on every save of docs/guide/*.md
cd src/Read2Me.Web && bun run dev             # in another terminal: the Overview picks the module up through HMR
```

Commit the regenerated module with the markdown.

## Conventions

ADR 0014 (`docs/adr/0014-native-web-front-end-replaces-angular.md`) records the decision. The
conventions that bite first:

- Light DOM. Component CSS is `@scope (tag) to (child-tag) { … }`, imported as text and passed to
  `adoptStyles()`. A plain `import './x.css'` breaks in production lazy chunks.
- Every element extends `R2mElement` and is registered with `define()`. Import element modules for
  their side effect; a type-only import never registers the element.
- Inputs are getter/setter pairs over a private signal. Outputs are DOM events from `emit()`, which
  bubble only when asked to.
- Links and `Router.navigate()` take app-relative paths (`projects/x/book`), resolved against the
  `<base href>`.
- Don't name an element member after an `HTMLElement` member (`remove`, `focus`, `hidden`, `title`).
- A spec that needs a real browser (dialog focus, popover, anchor positioning, layout) belongs in
  E2E; happy-dom has no `showPopover` and `showModal` only flips `open`.
