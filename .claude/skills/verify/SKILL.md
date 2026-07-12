---
name: verify
description: Launch ReadToMe and drive the Blazor UI in a browser to observe a change working end to end. Use when verifying a change to the app (settings pages, book tree, audio generation) rather than running tests.
---

# Verifying ReadToMe

Blazor Server app. The surface is the browser — drive it, don't import-and-call.

## Launch

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Read2Me.App --urls http://localhost:5099
```

Development config points the workspace at `D:\Dev\Read\WS`, which holds real projects with
generated audio (`foundation` ~314 wavs, `pride-and-prejudice` ~41,
`alices-adventures-in-wonderland` ~16). App settings (ffmpeg path, TTS configs) live in
`D:\Dev\Read\WS\app.db`, so a fresh run already has state.

Stop it with `taskkill //F //IM Read2Me.App.exe` — Ctrl-C from a background Bash task won't.

## Drive

No global playwright. Install `playwright-core` into the scratchpad (browsers are already in
`%LOCALAPPDATA%\ms-playwright`) and launch with `chromium.launch({ channel: 'chromium' })`:

```bash
cd <scratchpad> && npm i playwright-core
```

Blazor re-renders over SignalR, so a click's effect lands after the click resolves. Never
`waitForTimeout` and assume: `waitForFunction` on the DOM state you expect (a changed `src`, a
button label that flipped back). A too-early click gets silently dropped by in-flight guards and
you'll read stale state as if it were the result.

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

- **ffmpeg is not on PATH.** It lives at `D:\Dev\ffmpeg\bin\ffmpeg.exe`; the path is stored in
  settings (Audio Processing → ffmpeg). Any ffmpeg-dependent step silently *falls back* rather than
  failing, so an unset path looks like "the filter did nothing".
- **Docker AI containers are usually stopped.** TTS/LLM/Whisper flows need `docker compose up -d
  <service>` from `Infra/` first. Audio *post-processing* and assembly only need ffmpeg.
- **`<audio>` needs `Content-Length`.** A chunked response gives the element an infinite duration —
  it still plays, but shows no total time and no scrub bar. Check `a.duration`, not just that bytes
  arrive.
