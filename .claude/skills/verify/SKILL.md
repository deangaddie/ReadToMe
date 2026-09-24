---
name: verify
description: Launch ReadToMe and drive either UI in a browser — the Angular web app at /app (the default) or Blazor at /blazor — to see a change working or reproduce a bug against the real host. Use for verifying a change or hunting a bug in the running app rather than running tests.
---

# Verifying ReadToMe in a browser

Two UIs on one host: the Angular web app at `/app` (the default: `/` redirects there), Blazor Server at `/blazor` and its other root-level routes (dev server `:4200`
proxies to the host). The surface is the browser — drive it, don't import-and-call.

## Launch

```bash
dotnet run --project src/Read2Me.App --urls http://localhost:5000        # Development: no HTTPS redirect
cd src/Read2Me.Web && npm start                                          # only for web-app checks; :4200/app/
```

Development config points the workspace at `D:\Dev\Read\WS`, which holds real projects with
generated audio (`foundation` ~314 wavs, `pride-and-prejudice` ~41,
`alices-adventures-in-wonderland` ~16) and app settings in `app.db`. Mutate a throwaway project
(`throwawayProject` below), never those.

Start both detached with their output redirected to a file. A `dotnet run` piped through
`Select-Object -First N` gets killed when the pipe closes; a `| Select-Object -Last N` shows
nothing until exit. Stop the host with `taskkill //F //IM Read2Me.App.exe`.

## Drive

Use the checked-in harness `tools/browse/` (`npm ci` there once; it reuses the Chromium the E2E
tests installed). Read its `README.md` for the helper list and the example script; the helpers
cover launch with a watchdog, throwaway projects, the web app's node menus and dialogs, tree
waits, and Blazor's circuit wait.

Both UIs re-render after the click resolves — Blazor over SignalR, the web app from the hub's
receipt — so **wait on the DOM state you expect**, never on a timeout, and never on a count the
stale DOM already satisfies (a tree that shows two parts also has "two titles" before it reloads
as two volumes: wait on the exact titles).

Web-app selectors that are stable: `r2m-paragraph`, `r2m-item`, `.r2m-paragraph__menu`,
`r2m-node-menu`, `.mat-mdc-menu-panel [data-entry=...]`, `r2m-text-prompt-dialog`,
`r2m-confirm-dialog`, `.tree__title`, `.book__chapter`, `[data-testid=book-actions]`,
`.r2m-toast-panel`. Blazor: `.mud-treeview-item`, `.paragraph-hover-block`, `.mud-menu-item`
(force the click), `.mud-dialog`.

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
