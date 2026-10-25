---
name: verify
description: Launch ReadToMe and drive the web app (/app) in a browser to see a change working or reproduce a bug against the real host. Use for verifying a change or hunting a bug in the running app rather than running tests.
---

# Verifying ReadToMe in a browser

The web app (`src/Read2Me.Web`, custom elements on Bun, `docs/agents/web.md`) is served at
`/app`, and `/` redirects there. The surface is the browser — drive it, don't import-and-call.

## Launch

```bash
dotnet run --project src/Read2Me.App --urls http://localhost:5000 -- --Workspace:FolderPath="$PWD/tools/browse/workspace"   # from the repo root; Development: no HTTPS redirect; serves the /app bundle
cd src/Read2Me.Web && bun run dev                                        # optional HMR; :4300/app/ (Bun at ~/.bun/bin if not on PATH)
```

The host serves whatever bundle was last built (`pwsh scripts/build-web.ps1`); the dev server
serves the source. Restart `bun run dev` after adding a file reached through `@app/*`.

Always pass the tool workspace. The workspace in `appsettings.Local.json` belongs to the owner for
trying things out: leave it alone. `tools/browse/workspace/` is git-ignored and starts with a fresh
`app.db`; delete it to start clean. Its books come with the
tools: Project Gutenberg epubs in `tools/browse/books/`, imported with `bookProject('dracula')`
(or a text project from `throwawayProject`). Never use a book that is not from Project Gutenberg.
A fresh `app.db` takes its ffmpeg path from `Ffmpeg:Path` in `appsettings.Local.json` (or
`Ffmpeg__Path`). Without one it has none, so set it (Settings → Audio processing) before anything
that needs ffmpeg.

Start each detached with its output redirected to a file. A `dotnet run` piped through
`Select-Object -First N` gets killed when the pipe closes; a `| Select-Object -Last N` shows
nothing until exit. Stop the host with `taskkill //F //IM Read2Me.App.exe`.

## Drive

Use the checked-in harness `tools/browse/` (`bun install` there once; it reuses the Chromium the
E2E tests installed; run scripts with `bun x.mjs`). Read its `README.md` for the helper list and
the example script; the helpers cover launch with a watchdog, book and throwaway projects, the
node menu and dialogs, and tree waits.

`R2M_WEB` defaults to the dev server (`:4300`); `R2M_WEB=http://localhost:5000` drives the host's
own bundle instead:

```bash
bun my-check.mjs                                                       # on the dev server
R2M_WEB=http://localhost:5000 bun my-check.mjs                         # from the host's /app bundle
```

`tools/browse/shell-check.mjs` is the smallest smoke run: it imports Alice as a throwaway project,
prints the brand, title and whether the route rendered the not-found page, then removes it.

The web app re-renders after the click resolves, from the hub's receipt — so **wait on the DOM
state you expect**, never on a timeout, and never on a count the stale DOM already satisfies (a tree that shows two parts also has "two titles" before it reloads
as two volumes: wait on the exact titles).

Stable selectors: the `r2m-*` element names, BEM classes and `data-*` attributes —
`.r2m-paragraph`, `.r2m-item`, `.r2m-paragraph__menu`, `#r2m-node-menu-panel [data-entry=...]`
(menus are `popover`s with `role=menu`), `r2m-text-prompt-dialog`, `r2m-confirm-dialog`,
`.tree__title`, `.book__chapter`, `[data-testid=book-actions]`, `.r2m-toast`, and
`app-root .shell__brand` for the shell.

To compare audio, fetch inside the page (same origin) and hash — sizes match for same-length PCM,
so length alone proves nothing:

```js
await page.evaluate(async url => {
  const buf = await (await fetch(url)).arrayBuffer();
  const h = await crypto.subtle.digest('SHA-256', buf);
  return [...new Uint8Array(h)].map(b => b.toString(16).padStart(2, '0')).join('');
}, src);
```

## Gotchas

- **A frozen tab is a defect, not a slow page.** A screenshot that times out means the renderer is
  spinning (a microtask loop in a store); capture the API calls the page made and look for a loop.
- **ffmpeg is not on PATH.** It lives at `D:\Dev\ffmpeg\bin\ffmpeg.exe`; the path is stored in
  settings (Audio Processing → ffmpeg), so a fresh tool workspace needs it set. Any ffmpeg-dependent step silently *falls back* rather than
  failing, so an unset path looks like "the filter did nothing".
- **Docker AI containers are usually stopped.** TTS/LLM/Whisper flows need `docker compose up -d
  <service>` from `Infra/` first. Audio *post-processing* and assembly only need ffmpeg.
- **`<audio>` needs `Content-Length`.** A chunked response gives the element an infinite duration —
  it still plays, but shows no total time and no scrub bar. Check `a.duration`, not just that bytes
  arrive.
