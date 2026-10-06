# Read2Me native web

The front end ReadToMe is moving to: custom elements on Bun, with no framework (ADR 0014). It is
being ported screen by screen from the Angular app in `src/Read2Me.Web`, and is served at `/app2`
until cutover. Like the Angular app it is **not** part of `Read2Me.slnx`; `dotnet build` never
touches it.

## Prerequisites

- Bun 1.4.2, pinned by `packageManager` in `package.json`. No Node is needed.
- A running ReadToMe host on `http://localhost:5000` for anything that talks to the API
  (`dotnet run --project src/Read2Me.App` from the repo root).

## Commands

```bash
bun install --frozen-lockfile   # install exactly what bun.lock says
bun run dev          # dev server with HMR → http://localhost:4300/app2/ (proxies /api, /hubs, /workspace, /openapi, /audio-preview, /preview-source to :5000)
bun run build        # production build → ../Read2Me.App/wwwroot/app2/ (git-ignored)
bun run check        # lint + typecheck + api:check + tests + build — must be green before a PR
bun run lint         # oxlint, type-aware, with eslint-plugin-lit
bun run typecheck    # tsc --noEmit
bun test             # bun test over happy-dom; `bun test src/app/core/router.spec.ts` for one file
bun run format       # oxfmt
bun run api:types    # regenerate src/app/api/schema.d.ts from the host's /openapi/v1.json
bun run api:check    # fail if schema.d.ts is stale against the running host (skips with a warning when the host is down)
```

Restart `bun run dev` after adding a file that is reached through the `@app/*` alias: the dev
bundler does not see it until then.

## Layout

- `base.ts` — `BASE`, the path the app is served under. `build.ts` and `dev.ts` both read it, and
  `base.spec.ts` holds `index.html`'s `<base href>` to it.
- `src/app/core/` — the "framework": the signals adapter, the `R2mElement` base class and
  `define()`, `use`/`provide`/`token` services, the Navigation-API router and `<r2m-outlet>`,
  `openDialog`, `adoptStyles`, `Emitter`.
- `src/app/ui/` — shared elements and partials: the measured list, speaker chip and menu, dialogs,
  toast, tooltip, `icon()`, `statusChip()`, `emptyState()`, `countBadge()`.
- `src/app/api/` — `ApiClient` (the only place that calls `fetch`), the wire shapes in `dtos.ts`,
  the `BookCommand` union and the generated `schema.d.ts` (checked in).
- `src/app/live/` — the `/hubs/live` client. `live-messages.spec.ts` reads the C# hub sources and
  fails when a family or `kind` string drifts.
- `src/app/shell/`, `src/app/pages/` — the shell frame and the screens. Until a screen is ported
  its route renders a placeholder that links to the Angular app.
- `src/styles/global.css` — design tokens, fonts and the global primitive CSS.
- `src/testing/` — test helpers (a fake Navigation API for router-backed specs).

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
