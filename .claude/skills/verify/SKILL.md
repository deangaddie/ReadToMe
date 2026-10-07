---
name: verify
description: Launch ReadToMe and drive a web app (Angular at /app, or the native app at /app2) in a browser to see a change working or reproduce a bug against the real host. Use for verifying a change or hunting a bug in the running app rather than running tests.
---

# Verifying ReadToMe in a browser

Two apps run side by side while the native migration is in progress (`docs/agents/web.md`):
the Angular app at `/app` (`/` redirects there; frozen, bug fixes only) and the native app at
`/app2`. **Drive `/app2` for a screen that has been ported to the native app, `/app` otherwise.**
A native route that is not ported yet renders a placeholder linking to the Angular app, so if
you land on "This screen has not moved to the native app yet", switch to `/app`. The surface is
the browser — drive it, don't import-and-call.

## Launch

```bash
dotnet run --project src/Read2Me.App --urls http://localhost:5000        # Development: no HTTPS redirect; serves both bundles
cd src/Read2Me.Web && npm start                                          # optional Angular live reload; :4200/app/
cd src/Read2Me.Native && bun run dev                                     # optional native HMR; :4300/app2/ (Bun at ~/.bun/bin if not on PATH)
```

The host serves whatever bundle was last built (`pwsh scripts/build-web.ps1`,
`pwsh scripts/build-native.ps1`); a dev server serves the source. Restart `bun run dev` after
adding a file reached through `@app/*`.

Development config points the workspace at `D:\Dev\Read\WS`, which holds real projects with
generated audio (`foundation` ~314 wavs, `pride-and-prejudice` ~41,
`alices-adventures-in-wonderland` ~16) and app settings in `app.db`. Mutate a throwaway project
(`throwawayProject` below), never those.

Start each detached with its output redirected to a file. A `dotnet run` piped through
`Select-Object -First N` gets killed when the pipe closes; a `| Select-Object -Last N` shows
nothing until exit. Stop the host with `taskkill //F //IM Read2Me.App.exe`.

## Drive

Use the checked-in harness `tools/browse/` (`bun install` or `npm ci` there once; it reuses the
Chromium the E2E tests installed; run scripts with `bun x.mjs` or `node x.mjs`). Read its
`README.md` for the helper list and the example script; the helpers cover launch with a watchdog,
throwaway projects, the web app's node menus and dialogs, and tree waits.

Pick the app with `R2M_APP` (default `/app`); `R2M_WEB` defaults to that app's dev server
(`:4200` for `/app`, `:4300` for `/app2`) and `R2M_WEB=http://localhost:5000` drives the host's
own bundle instead:

```bash
R2M_WEB=http://localhost:5000 bun my-check.mjs                         # Angular, from the host's /app bundle
R2M_APP=/app2 bun my-check.mjs                                         # native, on its dev server
R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun my-check.mjs           # native, from the host's /app2 bundle
```

Under Git Bash set `MSYS_NO_PATHCONV=1` first (or write `R2M_APP=app2`): it rewrites a value
starting with `/` into a Windows path, and the page then 404s. `tools/browse/shell-check.mjs` is
the smallest smoke run: it prints the brand, title and whether the route is still a placeholder.

The web app re-renders after the click resolves, from the hub's receipt — so **wait on the DOM
state you expect**, never on a timeout, and never on a count the stale DOM already satisfies (a tree that shows two parts also has "two titles" before it reloads
as two volumes: wait on the exact titles).

Stable selectors: `r2m-paragraph`, `r2m-item`, `.r2m-paragraph__menu`,
`r2m-node-menu`, `.mat-mdc-menu-panel [data-entry=...]`, `r2m-text-prompt-dialog`,
`r2m-confirm-dialog`, `.tree__title`, `.book__chapter`, `[data-testid=book-actions]`,
`.r2m-toast-panel`. The native app keeps the `r2m-*` element names, BEM classes and `data-*`
attributes but has no Material markup: its menus are `popover`s, its toasts `.r2m-toast`, and
`app-root .shell__brand` is the shell. The browse helpers that reach into `.mat-mdc-*` are
Angular-only until the reader is ported.

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
  settings (Audio Processing → ffmpeg). Any ffmpeg-dependent step silently *falls back* rather than
  failing, so an unset path looks like "the filter did nothing".
- **Docker AI containers are usually stopped.** TTS/LLM/Whisper flows need `docker compose up -d
  <service>` from `Infra/` first. Audio *post-processing* and assembly only need ffmpeg.
- **`<audio>` needs `Content-Length`.** A chunked response gives the element an infinite duration —
  it still plays, but shows no total time and no scrub bar. Check `a.duration`, not just that bytes
  arrive.
