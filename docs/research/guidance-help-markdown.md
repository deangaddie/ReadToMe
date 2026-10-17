# Help panel markdown: build-time versus runtime rendering

Research for `.scratch/guidance-primitives/issues/01-markdown-rendering-options.md` (map: `.scratch/guidance-primitives/map.md`). Gathered 2026-10-17 against Bun 1.4.2 (the `packageManager` pin), the Bun and lit docs, npm, bundlephobia and the projects' GitHub repos, plus throwaway experiments run in a scratch folder (not committed). It compares two pipelines for rendering repo-authored help markdown (proposed home `docs/guide/`) in the lit-html web app and ends with a comparison table and a recommendation; the decision is ticket 02.

- **Pipeline A, build-time**: the Bun build turns the markdown into bundled HTML strings or lit-html template modules. No parser ships to the browser, no host endpoint.
- **Pipeline B, runtime**: the host serves the markdown; the web app renders it in the browser with a vendored parser.

## 1. Facts about the toolchain today

| Fact | Evidence |
|---|---|
| Runtime dependencies are exactly `lit-html`, `@preact/signals-core`, `@microsoft/signalr`; adding one needs an ADR | `docs/agents/web.md` opening paragraph; `src/Read2Me.Web/package.json` |
| The build is `Bun.build({ entrypoints: ['index.html'], target: 'browser', splitting: true, minify: true })` with no plugins; `bun run check` already runs it offline (the only network steps, `api:check` and `icons`, skip or are check-only offline) | `src/Read2Me.Web/build.ts`; `package.json` `check` script; `scripts/check-api-types.ts` (skips when no host), `scripts/icons.ts` (`--check` is offline) |
| The dev server is `Bun.serve({ development: { hmr: true }, routes: { '/app/*': index } })` over `import index from './index.html'`, cwd `src/Read2Me.Web` | `src/Read2Me.Web/dev.ts` |
| The app already inlines files at build time with import attributes: `import bookCss from './book.css' with { type: 'text' }`, typed by `src/css.d.ts` (`declare module '*.css'`) | `src/app/pages/book/book-page.ts:50`, `src/app/activity/styles.ts:2`, `src/css.d.ts` |
| `lit-html` already ships `directives/unsafe-html.js` (838 bytes unminified) and `static.js` (996 bytes) | `node_modules/lit-html/directives/unsafe-html.js`, `node_modules/lit-html/static.js` |
| The host serves `wwwroot` with `UseStaticFiles` (hashed bundle assets immutable) and a second `PhysicalFileProvider` for `/workspace`; `/app/*` falls back to the bundle's `index.html`; the App csproj embeds nothing | `src/Read2Me.App/Startup.cs` lines 70–90, 104–120; `src/Read2Me.App/Read2Me.App.csproj` |
| The E2E host uses a per-test `wwwroot` under a temp workspace into which the built bundle is copied | `src/Read2Me.E2eTests/Infrastructure/E2eAppFixture.cs:43–57`, `E2eTestBase.cs:13` |
| Bun 1.4.2 has a native markdown parser, `Bun.markdown` (`html`, `ansi`, `render`, `react`), and a bundler loader for `.md`/`.markdown` (`import html from './x.md' with { type: 'md' }` exports the rendered HTML string) | `bun -e 'console.log(Object.keys(Bun.markdown))'` → `html, ansi, render, react` on 1.4.2; `node_modules/bun-types/bun.d.ts` lines 1564–2050; `node_modules/bun-types/ts7.1/import-attributes.d.ts` (`declare module "*" with { type: "md" }`) |
| `Bun.YAML.parse` exists on 1.4.2 (for front matter without a dependency) | `bun -e 'console.log(typeof Bun.YAML.parse)'` → `function`; https://bun.com/docs/runtime/yaml |

### `Bun.markdown` in detail (primary sources)

- Shipped in Bun v1.3.8 (2026-01-29) as a Zig port of md4c, "a fast, CommonMark-compliant Markdown parser"; the PR also wired `.md` as a bundler loader — https://bun.com/blog/bun-v1.3.8, https://github.com/oven-sh/bun/pull/26440. The docs now describe it as written in Rust after the 1.4 rewrite and mark it **"Unstable API — under active development and may change"** — https://bun.com/docs/runtime/markdown.
- Defaults: GFM `tables`, `strikethrough`, `tasklists` on; `autolinks`, `headings` (ids / autolink), `noHtmlBlocks`, `noHtmlSpans`, `tagFilter` off — https://bun.com/docs/runtime/markdown and `bun-types/bun.d.ts` `namespace markdown { interface Options }`. Raw HTML and `javascript:` hrefs pass through verbatim: "The HTML output is not sanitized" — https://bun.com/blog/bun-v1.4. Confirmed locally: `Bun.markdown.html('[x](javascript:alert(1)) <b>raw</b>')` emits both unchanged.
- Loader contract: "Default for `.md` and `.markdown`… Bun renders the file to HTML and returns the HTML as a string… During bundling, Bun inlines the rendered HTML into the bundle as a string" — https://bun.com/docs/runtime/file-types#md. The loader takes **no options** and does **not** strip front matter: issue https://github.com/oven-sh/bun/issues/26605 is open and the front-matter PR https://github.com/oven-sh/bun/pull/36922 is unmerged. Confirmed locally: a file starting with `---\nstep: import\n---` bundles as `<hr /><h2>step: import…</h2>`.
- `bun-types` 1.4.3 types the `md` import attribute only under TypeScript ≥ 7.1 (`typesVersions` → `ts7.1/`); the project's `bun --bun x tsc` reports 6.0.3, so a `.md` import fails typecheck with "Cannot find module" until a `declare module '*.md'` shim like `src/css.d.ts` exists (verified in the scratch experiment with the project's `tsconfig.json`). Also https://bun.com/blog/bun-v1.4.1 ("With TypeScript 7.1 or later, bun-types types an import by its `with { type }` attribute").
- 1.4.3 (2026-10-10) fixed a quadratic-time `Bun.markdown.render()` case and three emphasis/link bugs — https://bun.com/blog/bun-v1.4.3. 1.4.2 itself touched nothing here — https://bun.com/blog/bun-v1.4.2.

## 2. Candidate libraries (for pipeline B, or for pipeline A if `Bun.markdown` is rejected)

Sizes are bundlephobia's min+gzip for the stated version (`https://bundlephobia.com/api/size?package=<name>@<ver>&record=true`), dates and dependency counts from the npm registry JSON (`https://registry.npmjs.org/<name>`), maintenance from the GitHub repo API (`pushed_at`, `open_issues_count`, which includes PRs; `archived`). "Wasm" sizes were measured by gzipping the shipped file from unpkg because bundlephobia counts only the JS glue.

| Library | Version (date) | min+gzip | Deps | ESM | Raw HTML / `javascript:` by default | Maintenance | Notes |
|---|---|---|---|---|---|---|---|
| `marked` https://github.com/markedjs/marked | 18.1.1 (2026-10-12) | 13.8 KB | 0 | yes (`type: module`, `exports`) | both pass through; docs: "Marked does not sanitize the output HTML", recommends DOMPurify; `sanitize` option removed in v8 — https://marked.js.org/ , https://marked.js.org/using_advanced | active: pushed 2026-10-14, 28 open | CommonMark 0.31 100 %, GFM 0.29 99 % (tables, task lists, fences, autolinks) — https://marked.js.org/ ; ESM CDN build documented |
| `micromark` https://github.com/micromark/micromark | 4.0.3 (2026-09-26) | 15.2 KB | 18 | ESM-only | **safe**: `allowDangerousHtml` and `allowDangerousProtocol` default `false`, "makes any markdown safe by default" — https://github.com/micromark/micromark#options | active: pushed 2026-09-26, 31 open | 100 % CommonMark; GFM tables/task lists need `micromark-extension-gfm` (more packages); README's own claim "smallest CM parser at ±14kb" |
| `markdown-it` https://github.com/markdown-it/markdown-it | 15.0.3 (2026-10-16) | 40.8 KB (44.1 KB for the self-contained browser ESM, measured) | 6 | dual (`exports` import/require, `./browser` ESM) | **safe-ish**: `html: false` default; `validateLink` blocks `javascript:`, `vbscript:`, `file:`, non-image `data:` — https://github.com/markdown-it/markdown-it/blob/master/docs/safety.md | active: pushed 2026-10-16, 10 open | 100 % CommonMark; tables in default preset, task lists via plugin |
| `snarkdown` https://github.com/developit/snarkdown | 2.0.0 (2020-08-31) | 1.1 KB | 0 | `module` field, no `exports` | both pass through; README "doesn't sanitize HTML"; `&` never escaped | dormant: last push 2022-11-29, 45 open, no release in 6 years | not CommonMark; "Tables are not yet supported"; fenced code yes |
| `md4w` https://github.com/ije/md4w (md4c → Wasm) | 0.2.7 (2025-05-28) | 1.8 KB JS + 27.4 KB wasm (`md4w-small`, measured) | 0 | yes | HTML passes (`NO_HTML` flag off by default); `javascript:` unverified | light: pushed 2025-05-28, 4 open | md4c: CommonMark 0.31, GFM tables/task lists/strikethrough; the only one with an explicit Bun claim; needs `await init()` and wasm URL wiring in a bundler |
| `markdown-wasm` https://github.com/rsms/markdown-wasm (md4c → Wasm) | 1.2.0 (2021-07-01) | 4.3 KB JS + 26.0 KB wasm (measured; README "31 kB") | 0 | `module` field, no `exports` | HTML passes; `javascript:` **stripped** unless `allowJSURIs` — https://github.com/rsms/markdown-wasm/blob/master/src/md.js | dormant: last push 2022-11-22 | md4c; `await markdown.ready` |
| `commonmark` (commonmark.js) https://github.com/commonmark/commonmark.js | 0.31.2 (2024-09-19) | 48.3 KB | 3 | `type: commonjs` with `exports` | passes through unless `HtmlRenderer({ safe: true })` | pushed 2026-10-13, 48 open, last release 2024 | reference implementation; CommonMark only, no tables |
| `remarkable` https://github.com/jonschlinkert/remarkable | 2.0.1 (2020-05-25) | 13.8 KB | 2 | `module` field, no `exports` | HTML disabled by default; `javascript:` undocumented | dormant: pushed 2024-05-17, 134 open | GFM tables/strikethrough on by default |
| `showdown` https://github.com/showdownjs/showdown | 2.1.0 (2022-04-21) | 25.0 KB | 1 | no (UMD `main` only) | passes through ("doesn't sanitize the input"); `safeMode` opt-in | pushed 2026-10-16, 236 open, README asks for maintainers | tables/task lists off unless `gfm` flavour |
| `nano-markdown` https://github.com/Holixus/nano-markdown | 1.2.2 (2023-03-07) | 1.0 KB | 0 | no (UMD/CJS) | passes through; `href` unfiltered | dormant: pushed 2023-03-07 | no fences, no tables |
| `drawdown` https://github.com/adamvleggett/drawdown | not on npm (`registry.npmjs.org/drawdown` 404) | ~4.7 KB unminified single file | 0 | no (global function) | escapes `<`/`>` first (blocks tags); `javascript:` passes | dormant: pushed 2020-12-22 | fences, tables, strikethrough |
| **`Bun.markdown`** (built in) https://bun.com/docs/runtime/markdown | Bun 1.3.8+ (pinned 1.4.2) | **0 KB** in the browser (build-time only) | 0 | n/a | passes through (`noHtmlBlocks`/`noHtmlSpans`/`tagFilter` off) — https://bun.com/blog/bun-v1.4 | shipped with Bun; "Unstable API" | md4c port: CommonMark + GFM tables/strikethrough/task lists; no front matter |
| DOMPurify (sanitiser baseline) https://github.com/cure53/DOMPurify | 3.4.17 (2026-10-12) | 11.5 KB | types only | yes | n/a | active: pushed 2026-10-16, 12 open | needs a DOM: under Bun that means jsdom ("happy-dom not considered safe"), so sanitising belongs in the browser |

The remark/mdast stack (unified + remark-parse + remark-rehype + rehype-stringify) sits on top of micromark and is strictly heavier; not measured. None of the npm libraries states Bun support except `md4w`; the plain-ESM ones (`marked`, `micromark`, `markdown-it`) need no DOM and run under Bun in principle, which is only relevant if a build step (pipeline A) uses one of them instead of `Bun.markdown`.

Browser-native sanitisation (`Element.setHTML`, the Sanitizer API) shipped in Firefox 148 (2026-02-24) and Chromium 146 (2026-03) but not Safari, per MDN's compat data — https://developer.mozilla.org/en-US/docs/Web/API/HTML_Sanitizer_API , https://hacks.mozilla.org/2026/02/goodbye-innerhtml-hello-sethtml-stronger-xss-protection-in-firefox-148/ . This project supports evergreen Chromium and Firefox only (`docs/agents/web.md`), so `setHTML` would be usable here if sanitising were wanted at all, without lit's `unsafeHTML`; §3 argues it is not needed.

## 3. Sanitisation when the source is repo-controlled

- Every candidate, including `Bun.markdown`, passes raw inline HTML through by default or by option, and none of them rejects `javascript:` URLs except where noted in §2 (micromark escapes HTML and drops dangerous protocols by default; markdown-it has `html: false`; marked and `Bun.markdown` pass everything). Sources: https://bun.com/blog/bun-v1.4, https://github.com/micromark/micromark#options, https://github.com/markdown-it/markdown-it/blob/master/docs/safety.md, https://marked.js.org/.
- lit's `unsafeHTML` sets `innerHTML` and its contract is "the string passed to `unsafeHTML` must be developer-controlled and not include untrusted content" — https://lit.dev/docs/templates/directives/#unsafehtml. Repo-authored markdown that goes through code review is developer-controlled in exactly that sense: the author of `docs/guide/*.md` is the same trust domain as the author of `*.ts`. A sanitiser (DOMPurify, §2) defends against *untrusted* input and buys nothing here beyond a linter for mistakes.
- What is worth doing instead, cheaply: in pipeline A the build step can fail the build when the rendered HTML contains `<script`, `on*=` attributes or a `javascript:` href (a regex over a string the build already holds), and can pass `tagFilter: true` / `noHtmlBlocks` to `Bun.markdown.html()` so authors cannot embed arbitrary tags by accident. In pipeline B the same check has to run in the browser on every render, or be dropped.
- Pipeline B has one extra surface: whatever the host serves at the markdown URL is rendered as HTML in the app. If the file is read from disk at runtime, anyone who can write that path can inject markup. That is the same trust as the bundle folder today, so not new, but it is a runtime path rather than a reviewed build artefact.

## 4. Pipeline A: build-time conversion

### 4a. Three ways to emit the modules

| Variant | Mechanism | What `bun run check` sees | Tested on 1.4.2 |
|---|---|---|---|
| **A1 native loader** | `import html from '@app/../help/import.md' with { type: 'md' }` per file; no build.ts change | Nothing new; the build already inlines `type: 'text'` CSS the same way; offline | Yes: bundle holds the rendered HTML string; front matter is rendered as `<hr><h2>` (not stripped); needs `declare module '*.md'` |
| **A2 build plugin** | `Bun.build({ plugins: [helpPlugin] })` where `onResolve` maps `virtual:help` (or `/\.md$/`) to a namespace and `onLoad` reads `docs/guide/*.md` with `Bun.Glob`, splits `---` front matter with `Bun.YAML.parse`, renders with `Bun.markdown.html(body, opts)` and returns `{ contents: 'export default {…}', loader: 'js' }` | Offline (all native APIs); plugins run only in the `Bun.build` JS API and the dev server's `[serve.static] plugins`, never the `bun build` CLI (https://bun.com/docs/bundler/plugins, https://bun.com/docs/bundler/fullstack#plugins) | Yes: a virtual module `{ 'import.md': { meta: { step, title }, html } }` landed in the browser bundle; the same plugin loaded in the dev server through `bunfig.toml` `[serve.static] plugins = ["./help-plugin.ts"]` |
| **A3 prebuild script** | `scripts/help.ts` (like `scripts/icons.ts`) reads `docs/guide/*.md` and writes a generated, committed `src/app/help/help.generated.ts` (`export const HELP = { import: { title, html } } as const`), plus a `help:check` that fails when the generated file is stale (the `icons:check` pattern) | Offline; the gate runs `help:check` instead of regenerating, exactly as `icons:check` does today | Not run; it is plain `Bun.Glob` + `Bun.file` + `Bun.write` (https://bun.com/docs/runtime/glob, https://bun.com/docs/runtime/file-io) |

Emitting lit templates instead of HTML strings: a generated module can `export const page = html\`<h1>…</h1>\`` with no expressions; a `TemplateResult` is just `{ _$litType$, strings, values }` and renders by identity of its `strings` array (https://lit.dev/docs/api/templates/). That saves the `unsafeHTML` call but every HTML string then becomes a tagged-template literal needing backtick/`${` escaping, and it is no smaller. Rendering the string through `unsafeHTML(html)` (lit already in the bundle, +~0.4 KB gzipped for the directive) is the simpler route; `static-html`'s `unsafeStatic` is the wrong tool (each distinct value creates and caches a new template, https://lit.dev/docs/templates/expressions/#static-expressions).

### 4b. Hot reload under `bun run dev`

Measured with a scratch `Bun.serve` dev server on 1.4.2, fetching the served bundle before and after editing the `.md`:

- A `.md` imported with `type: 'md'` **inside the project directory** (`src/Read2Me.Web/...`): the edit logged `Reloaded in 1ms: src/main.ts + 1 more` and the served bundle carried the new text (HMR works; with no `import.meta.hot.accept()` the page fully reloads, https://bun.com/docs/bundler/hmr).
- The same file **outside the project directory** (`../docs/guide/import.md`, i.e. the repo-root `docs/` the ticket proposes): the dev server printed `warn: File …\docs\guide\import.md is not in the project directory and will not be watched` and the bundle never changed. The warning is Windows-only in Bun's watcher: `#[cfg(windows)] … is_parent_or_equal(self.top_level_dir(), file_path) == Unrelated → warn` — https://github.com/oven-sh/bun/blob/main/src/watcher/Watcher.rs (`append_file_assume_capacity`). On Linux/macOS the file would be watched; this project is developed on Windows.
- A **plugin virtual module** (A2) rebuilt in the dev server but did not re-run when the `.md` changed, because nothing tells Bun's watcher the virtual module depends on those files (`OnLoadResult` has no `watchFiles` field; `bun-types/bun.d.ts` `OnLoadResultSourceCode`/`OnLoadResultObject`). A restart of `bun run dev` picks edits up.
- A **generated `.ts`** (A3) is an ordinary module, so HMR reloads on it; the author's edit loop is `bun --watch scripts/help.ts` in a second terminal (runtime watch; `Bun.build()` itself has no `watch` option, only the CLI does, https://bun.com/docs/bundler#watch-mode).

Consequence for the file location: for HMR the markdown should live under `src/Read2Me.Web` (e.g. `src/Read2Me.Web/help/`), or the repo-root `docs/guide/` is accepted with a restart / regenerate step. `docs/guide/` could also be a symlink or junction, but Bun's watcher compares resolved paths and junction behaviour was not tested.

### 4c. Keying content by Pipeline step or page

- **Filename**: `help/import.md`, `help/structure.md`, … one per step id (the nine ids from `.scratch/new-user-guidance/issues/01-pipeline-step-table.md`), plus `help/page-cast.md` style names for pages. Variant A1 keys by import path; A2/A3 key by the glob's basename. No metadata parsing, no front matter, so A1 works unmodified. A `satisfies Record<PipelineStepId, …>` on the generated/virtual object makes a missing step a type error.
- **Front matter**: `---\nstep: import\ntitle: Import a book\n---`. Only A2/A3 can read it (split on the `---` fences, `Bun.YAML.parse` the block, render the rest); A1 would render the fences as a rule and a heading. Front matter is the right place for a title that differs from the first heading, an `order`, or `pages: [cast]` lists; otherwise it is overhead.
- Either way the step id is a compile-time constant, so the help panel's lookup `HELP[step.id]` is total and tree-shaking is irrelevant (one small object).

### 4d. Size and cost

- Bundle growth = the rendered HTML of every help file (a 600-word page renders to roughly 4–5 KB raw, ~1.5 KB gzipped) + `unsafe-html.js` (~0.4 KB gzipped). No parser.
- Build-time cost: one `Bun.markdown.html` call per file (native, microseconds).
- Risk: `Bun.markdown` is an unstable API (https://bun.com/docs/runtime/markdown); output could change between Bun versions. Mitigation: A3 commits the generated HTML so a Bun upgrade shows a reviewable diff; A1/A2 would silently re-render on each build. A later swap to a JS parser in the plugin or script (micromark or marked under Bun) is a local change.

## 5. Pipeline B: runtime rendering

### 5a. Serving the markdown from the host

- `docs/guide/` is outside the App's content root, so the host cannot serve it with the existing `UseStaticFiles` unless (i) the csproj copies it to `wwwroot/help/` at build/publish time, (ii) it is embedded with `<EmbeddedResource Include="..\..\docs\guide\*.md" />` + `<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>` and served from a `ManifestEmbeddedFileProvider` (package `Microsoft.Extensions.FileProviders.Embedded`) — https://learn.microsoft.com/en-us/aspnet/core/fundamentals/file-providers?view=aspnetcore-10.0 — or (iii) an agent-API endpoint (`GET /api/help/{step}`) reads the file. (ii) and (iii) keep working in the E2E host, whose `wwwroot` is a temp folder (`E2eAppFixture.cs`); (i) only works if the E2E fixture copies the folder as it copies the bundle.
- A new endpoint lands in `/openapi/v1.json`, so `schema.d.ts` must be regenerated (`bun run api:types`) and `api:check` gates it; a static file does not.
- The browser fetch goes through `ApiClient` (the only `fetch` caller, `docs/agents/web.md`), which today types responses as JSON ProblemDetails/DTOs, so a text endpoint needs a small addition there.

### 5b. The browser parser

- Adding any parser breaks the "three runtime dependencies" rule and needs an ADR (`docs/agents/web.md`). Vendoring (copying the source into `src/app/vendor/`) avoids the npm dependency but not the rule's intent, and makes `oxlint --type-aware` / `oxfmt --check` / `tsc` run over foreign code unless excluded.
- Sizes are in §2. The smallest credible options add roughly 1 KB (snarkdown, non-CommonMark) to 10–20 KB (marked, micromark, markdown-it) gzipped to the first page that opens the help panel; `splitting: true` lets it be a lazy chunk.
- Rendering is `unsafeHTML(parser(text))` either way; sanitisation per §3.

### 5c. Hot reload and keying

- Editing `docs/guide/*.md` needs no rebuild: the next fetch returns the new text, and the app re-renders when the panel re-opens (or on a dev-only refetch). `bun run dev` is not involved; the dev proxy forwards `/api` and `/workspace` to the host, so a `/help` prefix would be added to `FORWARDED` in `dev.ts`.
- Keying is by URL: `/help/<step>.md` by filename, or an index endpoint that lists front matter (parsed on the host with YamlDotNet — another dependency — or a trivial `---` splitter). A missing file is a runtime 404 instead of a compile error.

### 5d. Costs

- Two moving parts (host route or endpoint + browser parser) and a runtime failure mode (panel opens, fetch fails, needs an error state) for content that changes only with a release.
- Standalone guide: the same URL could feed an in-app `/help` route (and nothing else; a `.md` on the host is not a rendered page for a browser without the app).

## 6. Comparison

| Criterion | A1 native `md` loader | A2 build plugin (`Bun.markdown` + front matter) | A3 prebuild script (generated `.ts`, committed) | B runtime (host + vendored parser) |
|---|---|---|---|---|
| New runtime dependency / ADR | None | None | None | Yes (parser), ADR required |
| Bundle delta | rendered HTML + 0.4 KB | same | same | HTML fetched on demand + 1–20 KB parser chunk |
| `bun run check` offline | Yes | Yes | Yes (`help:check`, icons pattern) | Yes, but `api:check` touches a new endpoint if one is added |
| Host change | None | None | None | Static route or endpoint + E2E fixture wiring |
| Front matter | Rendered as text (no) | Yes (`Bun.YAML`) | Yes (`Bun.YAML`) | Only with a host parser or in-browser splitter |
| Keying | Import path / filename | Glob basename or front matter | Glob basename or front matter | URL path |
| Missing step detected | Typecheck (import) | Typecheck via `satisfies` | Typecheck via `satisfies` | Runtime 404 |
| HMR on `.md` edit (Windows) | Yes if under `src/Read2Me.Web`; no for `docs/guide/` (not watched) | No (virtual module; restart dev) | Yes once `bun --watch scripts/help.ts` rewrites the module | N/A: next fetch |
| Bun-version drift visible | No (re-rendered each build) | No | Yes (committed output diffs) | N/A (parser pinned) |
| Raw-HTML / `javascript:` guard | None at build (loader has no options) | `tagFilter`/`noHtml*` + build-time lint | same | In-browser lint or none |
| Extra tooling | `declare module '*.md'` | plugin file + bunfig `[serve.static]` for dev | script + check + generated file in git | none at build |
| Standalone guide | Same module can feed an in-app route; repo `.md` readable on GitHub | same | same | same |

## 7. Recommendation (reasons, not a decision)

Build-time wins on every criterion that matters to this repo: it keeps the runtime-dependency rule intact, needs no host work, stays offline in the gate, and turns a missing or misnamed help file into a compile error. The parser already exists in the pinned toolchain (`Bun.markdown`, md4c-derived CommonMark + GFM), so pipeline B's only real advantage, editing without a rebuild, is buying an edit loop the dev server already provides for files under the web project.

Within build-time, **A3 (prebuild script with a committed generated module and a `help:check`)** fits this codebase best:

- It is the pattern `scripts/icons.ts` / `icons:check` already uses, so there is one convention, not two.
- Committed output makes the Unstable `Bun.markdown` a reviewable risk: a Bun upgrade that changes rendering shows up as a diff, not as a silent UI change.
- It can read front matter and fail the build on raw `<script`/`on*=`/`javascript:` so authors cannot drift into HTML, and can key by step id with a `satisfies Record<PipelineStepId, …>`.
- It leaves the markdown free to live at repo root `docs/guide/` (readable on GitHub as the standalone guide) despite the Windows watcher limit, because the generated `.ts` is what the dev server watches; the authoring loop is `bun --watch scripts/help.ts`.

A1 is the zero-code alternative if front matter and the raw-HTML guard are not wanted and the files move under `src/Read2Me.Web/help/` for HMR; A2 adds a bunfig plugin entry and loses HMR for nothing A3 does not give. If the user rejects `Bun.markdown` because of its Unstable label, A3 is also the variant where swapping in a JS parser (micromark or marked, run under Bun at build time only) touches one script and no bundle.

What to settle in ticket 02: file location (`docs/guide/` vs `src/Read2Me.Web/help/`), filename vs front matter keying, whether the generated module holds HTML strings (rendered via `unsafeHTML`) or lit templates, and whether the same files feed an in-app `/help` route.

## Appendix: scratch experiments (2026-10-17, Bun 1.4.2, Windows 11)

1. `Bun.build` browser target, `import md from '../../docs/guide/import.md' with { type: 'text' }` → file inlined as a string, front matter included.
2. Same with `with { type: 'md' }` → rendered HTML inlined; front matter rendered as `<hr /><h2>step: import\ntitle: …</h2>`; `[js](javascript:alert(1))` and `<b>raw html</b>` passed through.
3. `Bun.build({ plugins: [helpIndex] })` with `onResolve(/^virtual:help-index$/)` + `onLoad` (`Bun.Glob` over `../docs/guide`, `Bun.YAML.parse` front matter, `Bun.markdown.html`) → `{ "import.md": { meta: { step, title }, html } }` in the bundle.
4. `Bun.serve` dev server with the `.md` under `../docs/guide` → `warn: File … is not in the project directory and will not be watched`; edits never reached the served bundle.
5. Same with the `.md` under the project folder → `Reloaded in 1ms: src/main.ts + 1 more`; served bundle updated.
6. Dev server with `bunfig.toml` `[serve.static] plugins = ["./help-plugin.ts"]` → virtual module served; `.md` edits did not trigger a rebuild.
7. `tsc --noEmit` with the project's `tsconfig.json` (TypeScript 6.0.3 in use): `.md` imports with `type: 'md'` and `type: 'text'` both fail with TS2307 until a `declare module '*.md'` shim exists.
