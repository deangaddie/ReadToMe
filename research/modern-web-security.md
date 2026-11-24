# Audit: security (HTML injection, Trusted Types, origins) against modern-web-guidance

Research for #458 (map #444). Read on 2026-11-24 against `main` at `13fc2cb2`.

**Guides read** (local markdown, skill version `2026_11_10-47b4c80e`, under `~/.claude/skills/modern-web-guidance/guides/security/`): `security`, `sanitize-untrusted-html`, `trusted-types`, `validate-origins`, `secure-external-subresources`, `restrict-outbound-connections`, `local-network-access`. No `npx` was run and nothing was installed.

**Code read**: `src/Read2Me.Web` (every HTML sink, URL binding, `target=_blank`, `postMessage`/origin use, the live connection, `index.html`, `scripts/help-render.ts`, `dev.ts`), the host that serves it (`src/Read2Me.App/Startup.cs`, `Live/LiveHub.cs`, `Live/LiveServiceCollectionExtensions.cs`, `Api/ProviderSettingsEndpoints.cs`, `Api/VoiceEndpoints.cs`, `Api/UploadedFile.cs`), and lit-html 3.3.3's Trusted Types policy (`node_modules/lit-html/development/lit-html.js`, from the main checkout's install; this worktree has no `node_modules`).

## Scope note for triage

The web client itself is already clean: it has one HTML sink (`unsafeHTML` over build-time help text), no `postMessage`, no origin comparisons, no external subresources, and no browser-side calls to the local network. **Every kept finding lands in the host** (`src/Read2Me.App`), because the security guides work through the response headers and request checks of the server that delivers `/app`, `/api`, `/hubs/live` and `/workspace`. Today the host sends none of them: a grep of `src/Read2Me.App` for `Content-Security-Policy`, `X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-*`, `AddCors`, `AllowedOrigins` and `Sec-Fetch` finds nothing. Only `UseHsts()` (outside Development) is there.

So the map's "one guide area per PR" rule and its "touches a shared primitive" column mostly don't apply: none of these touch a `ui/` primitive. Triage should decide whether host-side fixes belong on this map (the map's Destination names `src/Read2Me.Web`) or on a sibling issue.

**Threat model these findings assume.** Read2Me is a single-user local app on `http://localhost:5000` / `https://localhost:5001` with no authentication. The attacker is **any other web page the user has open**, in any tab, which can send requests to `localhost` and open WebSockets to it. In Chromium 145+ Local Network Access puts a permission prompt in front of public→loopback requests (`local-network-access`: "Supported by: Chrome 145 (Feb 2026) and Edge 145 (Feb 2026). Unsupported in: Firefox and Safari"). Firefox has no such prompt, so a Firefox user gets no browser-side protection at all.

None of the attacks below were reproduced: they come from reading the code against the guides. Each issue's throwaway proof should be the first thing to confirm the problem.

## Findings

Ranked most important first. Size: S ≈ under a day, one file; M ≈ a few files plus tests; L ≈ more.

### F1. The live hub accepts WebSocket connections from any origin — KEEP

- **Guide**: `security` §1.5 / CORS. "Always validate the `event.origin` of incoming messages … Do **not** trust wildcards (`*`), substring checks, or unverified payloads", and "Validate the `Origin` header on the server and set `Access-Control-Allow-Origin` dynamically to specific origins". `validate-origins` covers the comparison: "Require an exact `(scheme, host, port)` match when handling cross-window `postMessage` commands … or deciding whether to attach credentials".
- **Where**: `src/Read2Me.App/Live/LiveServiceCollectionExtensions.cs:39` (`endpoints.MapHub<LiveHub>(LiveHub.Path)` with no options) and `Live/LiveHub.cs:40-81` (`JoinProject`, `JoinStream`, `GetSnapshot`).
- **What goes wrong today**: CORS does not apply to a WebSocket handshake, and the hub checks no `Origin`. A page on any site can open `ws://localhost:5000/hubs/live` straight away (SignalR's client lets you skip negotiate when you pick the WebSockets transport). Then it can call `JoinStream("llm")` and get every LLM turn as it streams, which means the book's text, the attribution prompts and the model's output, with the current turn replayed (`LiveHub.cs:72-75`). It can also call `GetSnapshot()` and `JoinProject(folder)` for project status. The user is never told. I haven't checked whether the dev proxy (`src/Read2Me.Web/dev.ts:28`) forwards the browser's `Origin` on its WebSocket hop, so the fix has to be tested through the dev server too.
- **Fix shape**: reject the hub's upgrade (and negotiate) unless `Origin` is absent or exactly matches one of the host's own origins (`http://localhost:5000`, `https://localhost:5001`, the configured `Urls`, and in Development the Bun dev server `http://localhost:4300`). Compare whole origin strings for equality, never with `StartsWith`. A missing `Origin` (curl, an agent, a .NET client) is allowed. Small middleware ahead of `MapLiveHub`, or `WebSocketOptions.AllowedOrigins` plus the same check on `/hubs/live/negotiate`.
- **Firefox**: no fallback needed. The check is server-side and covers every browser, and Firefox is the browser that needs it most (no LNA).
- **Size**: S. Host only, no primitive.

### F2. The agent API takes cross-site writes with no Fetch Metadata or Origin check — KEEP

- **Guide**: `security` §3.7. "Implement a server-side resource isolation policy that checks `Sec-Fetch-*` headers and rejects `cross-site` requests for non-navigational endpoints", "Reject disallowed requests *before* authentication or authorization checks", "`same-site` trusts every subdomain under your eTLD+1 … drop `same-site` from the allowlist and accept only `same-origin` and `none`."
- **Where**: `src/Read2Me.App/Startup.cs:101-118` (the endpoint pipeline has no isolation step). The exposed writes include the multipart `POST` with antiforgery turned off, `Api/ProviderSettingsEndpoints.cs:71-72` (`POST …/transcription/{id}/test`), and every bodyless `POST`, for example `Api/VoiceEndpoints.cs:77` (`POST …/transcribe`).
- **What goes wrong today**: a cross-site page can send any "simple" request: a `POST` with no body, `multipart/form-data` or `text/plain`. Those go without a CORS preflight and still run on the server, even though the page can't read the response. So another tab can start LLM/TTS jobs, transcribe, or post a multipart upload. The voice-audio upload (`VoiceEndpoints.cs:73-75`, also antiforgery-off) is a `PUT`, which does need a preflight, so it is safe today only because no CORS policy exists. JSON-body endpoints need a preflight, which fails because there's no CORS policy, so those are safe as long as model binding demands `application/json`. Folder names and GUIDs have to be guessed, but an endpoint that takes no ids (settings tests, setup check) needs no guessing. The default Kestrel binding is loopback only, but in Firefox nothing at all stands between a web page and `localhost`.
- **Fix shape**: middleware on `/api`, `/hubs`, `/workspace`, `/audio-preview` and `/preview-source`. Let through any request without `Sec-Fetch-Site` (agents, curl, tests), and `same-origin` and `none`. Let through `cross-site` only for a `GET` navigation. Reject everything else with 403 and send `Vary: Sec-Fetch-Dest, Sec-Fetch-Mode, Sec-Fetch-Site`. Don't allow `same-site`: every `localhost` port counts as the same site, so any other local dev server would count as trusted. The Bun dev server at `:4300` is same-site with `:5000`, not same-origin, but its proxy forwards the browser's headers (`dev.ts:33`), and the browser computed `Sec-Fetch-Site` against `:4300`, so proxied calls arrive as `same-origin`. The throwaway proof must cover this.
- **Firefox**: the guide gives no compatibility data for Fetch Metadata. From my own knowledge, not the guide: `Sec-Fetch-*` ships in Firefox 90+ and Chromium 76+, so no fallback is needed. If `Sec-Fetch-Site` is missing, the middleware falls back to an exact `Origin` check like F1's.
- **Size**: S–M (one middleware plus tests that each upload and stream path still works). Host only.

### F3. No Content Security Policy on `/app` — KEEP

- **Guide**: `security` §3.2. "Mandatory directives to set: `script-src 'nonce-{RANDOM}' 'strict-dynamic' 'report-sample'` … `object-src 'none'` … `base-uri 'none'` … `frame-ancestors 'none'` … `require-trusted-types-for 'script'`", and "For static/cached HTML (SPAs) where a per-response nonce is not possible, use hash-based CSP". On nonce stamping: do not use "a regular expression … to stamp CSP nonces onto every `<script>` tag", though replacing "an explicit placeholder token in a static template before interpolating any user data" is allowed.
- **Where**: `src/Read2Me.App/Startup.cs:170-180` (`ServeAppAsync` sends `wwwroot/app/index.html` with no CSP), and the built bundle's `index.html` (source: `src/Read2Me.Web/index.html:10-11`, one module `<script src>` and one stylesheet link, no inline script).
- **What goes wrong today**: the app has no injection hole I could find (see "Already done" below), so CSP is defence in depth. Without it, a future mistake, such as a binding that reaches `unsafeHTML`, an `on*=` attribute string, or a script URL built from data, runs at once with the full agent API behind it. A user-uploaded file under `/workspace` is a risk too: the uploads are images and audio today, and served same-origin.
- **Fix shape**: the bundle is static, so either (a) have `ServeAppAsync` read `index.html`, swap a build-emitted placeholder (`__CSP_NONCE__` on the `<script>` tag) for a fresh nonce, and send `script-src 'nonce-…' 'strict-dynamic'`, or (b) hash. Option (a) is the guide's recommended form. Also send `object-src 'none'; frame-ancestors 'none'; require-trusted-types-for 'script'; trusted-types lit-html` (F4). **Watch out: `base-uri` must be `'self'`, not the guide's `'none'`.** `index.html:6` declares `<base href="/app/">` and the router resolves every path against `document.baseURI` (`src/app/core/router.ts:53,84`), so `base-uri 'none'` would break routing. Leave `style-src` unrestricted: lit templates carry `<style>` and `style=` bindings, and `wwwroot/app-missing.html:7` has an inline `<style>`. Ship it first as `Content-Security-Policy-Report-Only` and let the E2E suite fail on any console violation. Then enforce. There's no reporting collector for a local app, so leave `Reporting-Endpoints` out. The Bun dev server serves its own `index.html` (`dev.ts`), so either dev runs without the CSP or `dev.ts` has to send a matching header. Triage decides which.
- **Firefox**: CSP Level 3 `'strict-dynamic'`, nonces and `frame-ancestors` ship in Firefox and Chromium. The guide gives no data on these; that comes from my own knowledge.
- **Size**: M (a build step for the placeholder, the host handler, a report-only pass over the E2E suite in both browsers). Host plus `build.ts`, no primitive.

### F4. Trusted Types not enforced (no sinks left to migrate) — KEEP, ships with F3

- **Guide**: `trusted-types` §4. "Enforce Trusted Types using a Content Security Policy", and "You can also restrict which policies are allowed to be created". `security` §3.3 adds: "DO NOT: Create a pass-through policy (such as `createHTML: (s) => s`)".
- **Where**: the same CSP header as F3. The only sinks are lit-html's own (`node_modules/lit-html/development/lit-html.js:69-73` creates the `lit-html` policy with `createHTML: (s) => s`, and `:661` `el.innerHTML = html` for its template element). The app's own code has no `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, `eval`, `new Function` or string `setTimeout` (grep of `src/Read2Me.Web/src`, specs excluded).
- **What goes wrong today**: nothing is broken. Without enforcement, any later raw sink in app code or in a dependency update (`@microsoft/signalr`, `@preact/signals-core`, `lit-html`) is silently accepted. With `require-trusted-types-for 'script'; trusted-types lit-html`, such a write throws a TypeError at the line that did it.
- **The pass-through caveat**: lit's `lit-html` policy is exactly the identity policy the guide warns against. Lit argues that it only ever receives developer-authored template strings (`lit-html.js:62-68`). `unsafeHTML` gets around that by building a fake `strings` array (`directives/unsafe-html.js:32-34`), so the help text (`src/app/help/help.ts:26`) still renders under enforcement, through lit's policy. Enforcement therefore guards against **new raw sinks**. It doesn't guard what goes into `unsafeHTML`, and the help lint in `scripts/help-render.ts` stays the control for that.
- **Firefox**: ships. The guide says: "Baseline status for Trusted types: Newly available … Supported by: Chrome 83 … Firefox 148 (Feb 2026), and Safari 26". No tinyfill is needed for the browser gate.
- **Size**: S on top of F3 (two directives, plus an E2E check that a deliberate `innerHTML = 'x'` throws). No primitive.

### F5. Clickjacking: the app can be framed by any site — KEEP (S, or fold into F3)

- **Guide**: `security` §1.4. "**DO (Default — no framing needed)**: Set `Content-Security-Policy: frame-ancestors 'none'` alongside `X-Frame-Options: DENY`".
- **Where**: `Startup.cs:170-180` (`/app` responses), plus `/workspace` and `/api` if those headers apply to every response.
- **What goes wrong today**: another site can load `http://localhost:5000/app/projects/<folder>` in a transparent iframe and trick the user into clicking "Delete" in a project card menu or "Confirm" in the confirm dialog. The app never frames itself.
- **Fix shape**: send `X-Frame-Options: DENY` on every response, and `frame-ancestors 'none'` in F3's CSP. If F3 slips, `X-Frame-Options` alone is a one-line middleware worth shipping first.
- **Firefox**: ships in both (not from guide data).
- **Size**: S. Host only.

### F6. Companion headers: `nosniff`, COOP, CORP, Referrer-Policy — KEEP (S, one PR)

- **Guide**: `security`, companion policies. "Set `X-Content-Type-Options: nosniff`". COOP: "Use `Cross-Origin-Opener-Policy: same-origin-allow-popups`", or `same-origin` when nothing relies on `window.opener`. CORP: "Default to `Cross-Origin-Resource-Policy: same-origin` for app-internal responses". "Use `Referrer-Policy: strict-origin-when-cross-origin` as a safe default."
- **Where**: `Startup.cs:77-80` and `:133-149` (the two `UseStaticFiles` mounts, `/app` bundle and `/workspace`), plus the API.
- **What goes wrong today**:
  - **CORP** is the one that changes behaviour. A cross-site page can embed `<audio src="http://localhost:5000/workspace/<folder>/…wav">` or `<img src=…/workspace/<folder>/cover.jpg>` and play or show the user's files. It can't read the bytes, but it can tell whether a file exists from `load`/`error` events, which lets it probe folder names before F1/F2 attacks. `Cross-Origin-Resource-Policy: same-origin` on `/workspace`, `/audio-preview` and `/preview-source` blocks that.
  - **nosniff**: `/workspace` serves user-uploaded files (cover images are limited to `.jpg/.jpeg/.png/.webp` by `Api/UploadedFile.cs:12-19`, audio by `VoiceEndpoints.cs:60-63`) with types inferred from the extension. With `nosniff`, a mislabelled upload can't be read as script or style.
  - **COOP** `same-origin` cuts the `window.opener` link to any page that opened the app. The app's only popups are the help panel's GitHub links, which are `rel="noopener"` already (`src/app/pages/project/help-panel.ts:38-39`, `scripts/help-render.ts:40`).
  - **Referrer-Policy** is already the browser default (`strict-origin-when-cross-origin`). Setting it explicitly costs nothing, but it's optional.
- **Firefox**: all four ship (not from guide data).
- **Size**: S, one middleware. No primitive.

## Dropped

| # | Guide id and rule | Where | Reason |
|---|---|---|---|
| D1 | `sanitize-untrusted-html` §1, "Use `setHTML()` … to safely insert untrusted HTML" | `src/app/help/help.ts:26` (`unsafeHTML(entry.html)`) | **Already done.** The HTML isn't untrusted: `scripts/help-render.ts:12` renders repo markdown at build time with `noHtmlBlocks`/`noHtmlSpans`/`tagFilter`, rejects `javascript:`/`data:`/relative links and images (`:25-41`), fails on `<script` and `on*=` (`:44-47`), and `help:check` gates the generated module. `setHTML()` ships in both browsers ("Chrome 146 … Firefox 148"), so it would pass the gate as extra defence. But its default sanitizer would need checking against the `target`/`rel` attributes, and it would replace a safe-by-construction path with a runtime one for no user-visible gain. Revisit only if help ever takes runtime content. |
| D2 | `security` §1.2, "Prefer `textContent` … over `innerHTML`" | all of `src/Read2Me.Web/src` | **Already done.** The app has no raw sinks (see F4). lit-html escapes every text and attribute binding. |
| D3 | `security` §1.2 / `trusted-types` warning, "`<a href="${escaped}">`, where `javascript:` URLs are not neutralized" | every `href=${…}` and `.src=${…}` binding (e.g. `pages/project/project-details-panel.ts:163`, `pages/settings/setup-check/fix-hint.ts:13`, `shell/shell.ts:466`, `ui/partials.ts:63`) | **Already done.** Each URL is built from app constants plus `encodeURIComponent`'d ids, or comes from server code (`SetupFixHint.link.path`). None comes from a query parameter or user text. lit does not sanitise URLs, so a new binding fed by user data would be a real hole, and F3/F4 are the backstop for that. |
| D4 | `security` §1.1, "Serve a header like `Strict-Transport-Security`" | `Startup.cs:66` | **Already done** (`UseHsts()` outside Development; ASP.NET leaves `localhost` out of HSTS anyway). |
| D5 | `security` CORS, "DO NOT: Use wildcard `*`" | host | **Already done**: no CORS policy at all, so cross-origin reads are refused. Keep it that way: F1 and F2 rely on it. |
| D6 | `security` Permissions Policy, "Disable unused browser features (camera, geolocation, microphone)" | host | **Browser gate**: `Permissions-Policy` is Chromium-only (Firefox ignores the header, from my own knowledge; the guide gives no data). The app uses no powerful features and embeds no iframes, so the gain is near zero. |
| D7 | `restrict-outbound-connections`, the `Connection-Allowlist` header | host | **Browser gate**: "Supported by: Chrome 152 … Unsupported in: Firefox and Safari", and "it cannot be polyfilled". The guide's own Firefox fallback is CSP `connect-src`, which belongs with F3 if triage wants it (`connect-src 'self'` covers same-origin `ws:`/`wss:` in both browsers). |
| D8 | `validate-origins`, "use `Origin.from()` … `isSameOrigin()`" | client | **No client surface**: no `postMessage`, no `message` listener, no client-side origin comparison. The router's only check is a same-document `pathname` prefix after `NavigateEvent.canIntercept`, which is same-origin by definition (`src/app/core/router.ts:109-112,161-166`). The `Origin` API is also "Unsupported in: Firefox". The server-side counterpart is F1. |
| D9 | `security` §3.6, Cross-Origin Isolation | host | **No surface**: nothing uses `SharedArrayBuffer` or threaded WASM. |
| D10 | `security` §1.3 Secure Cookies, Clear-Site-Data (logout) | host | **No surface**: no cookies, no sign-in, no logout. |
| D11 | `security` §2.2 Reporting-Endpoints / `report-to` | host | **No surface** for a local single-user app (nothing collects reports). F3's report-only phase reads violations from the browser console in E2E instead. |

## Guides with no surface in the app

- **`secure-external-subresources`**: the bundle loads no third-party script, stylesheet, font or image. The icon font is a committed subset (`bun run icons`), and the CSS has no `@import` or `url()` to another origin. The only outbound link is the help panel's `target=_blank` navigation to GitHub, which is a navigation, not a subresource, so SRI and `crossorigin` don't apply. A grep for `https?://` in `src/Read2Me.Web/src` finds only that link and `http://localhost` placeholder text in the settings forms.
- **`local-network-access`**: the browser never calls the local network. The AI services (`:8080`, `:8004`, …) are called by the .NET host, not the page, and the page is itself served from loopback, so LNA doesn't apply to it ("serving your web app from `http://localhost` … will **not** trigger LNA checks"). LNA matters here only as the partial, Chromium-only browser defence that F1/F2 must not rely on.
- **`restrict-outbound-connections`**: applies only as a host header (D7). The client has no fetch to another origin to restrict.

## Suggested grouping for triage

One host-side "security headers and request isolation" effort, in this order:

1. **F1 + F2** (S–M): server-side origin and Fetch Metadata checks. These close the only live, user-reachable exposure (another tab reading the LLM stream, or firing writes), and they work in every browser.
2. **F5 + F6** (S): static companion headers, one middleware.
3. **F3 + F4** (M): CSP with a nonce and Trusted Types enforcement, report-only first. This is defence in depth: the client has no hole today.
