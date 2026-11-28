# Agent API

HTTP API for driving the full audiobook production cycle without the UI. Minimal
APIs inside the app process: start the app (`dotnet run --project src/Read2Me.App`)
and talk to `http://localhost:5000/api/...`. Localhost only, no auth.

**Shapes**: this guide names endpoints, order and behaviour. Request and response schemas live
in the OpenAPI document — fetch `GET /openapi/v1.json` first and treat it as the
source of truth (ADR 0017). Fields are spelled out here only where their meaning isn't
in the schema, or where the shape is not in the document (stored step settings).

**Responses**: every `/api/` operation declares what it answers. A 2xx carries a JSON
schema, or a file's content type (`audio/wav`, `audio/mp4`, an image); only a `204`, or
a bare `202` with nothing to report, has no body. Responses don't omit null fields: an
empty value is written as `null`.

**Wire form**: one for every endpoint and the hub. An enum is its C# member name in
PascalCase (`"Chapter"`, `"Confirmed"`, `"VoxCpm2"`), in bodies, route segments, query
values and dictionary keys, and the document lists the members. Names match exactly in
both directions, so `nodes/chapter/...` is a 400. An integer for an enum, or a string for
a number (`"3"`), is a 400 too.

**Errors**: every non-2xx is an RFC 7807 ProblemDetails (`application/problem+json`),
declared once as the `default` response of every operation in the document rather than
per status. 400 malformed input, 404 unknown folder/entity, 409 already-running, blocked or refused by state (e.g. a series name taken)
(assembly carries `audioRemainingCount`; a Project activity gate refusal carries `activity`: `busy`, `restoring` or `deleting`), 422 domain failure (message in `detail`).

**Refused mutations**: every endpoint that commits a Book mutation answers a refusal by its
Refusal kind (`context/book-structure.md`), the same whichever route asked: contention (another
write holds the project, the Project activity gate, a linked book that can't take it, a clash
such as a Pronunciation's written form) **409**, missing **404**, invalid **422**. A cancelled
request gets no answer. The command endpoint keeps two older answers (ADR 0007): a refusal a
command has always softened is `200 { "newEntityId": null }`, and a missing thing a strict
command names is **422**, since that endpoint's 404 means an unknown project.

**Polling pattern**: the four long operations (attribution, audio, voice batch,
assembly) return `202 Accepted` and run in the background. Poll their status
endpoint until idle, then read per-item outcomes:

```bash
# generic wait loop (attribution shown; same shape for /api/audio/queue)
while :; do
  s=$(curl -s http://localhost:5000/api/attribution/queue)
  q=$(echo "$s" | jq '.queuedCount + .processingCount')
  [ "$q" = "0" ] && break
  sleep 5
done
```

## Request isolation and security headers

The host has no authentication, so any other page the user has open is the threat, another
`localhost` port included (it is the same site). One middleware (`Api/RequestIsolation.cs`), ahead
of routing and every file mount, guards `/api`, `/hubs` (negotiate and the WebSocket upgrade),
`/workspace`, `/audio-preview` and `/preview-source`:

- **`Sec-Fetch-Site` present**: `same-origin` and `none` pass. Any other value passes only for a
  `GET` navigation (`Sec-Fetch-Mode: navigate`, not into an `<object>` or `<embed>`), so a link to a
  Workspace file still opens. Everything else, `same-site` included, is a **403** problem.
- **No `Sec-Fetch-Site`, `Origin` present**: `Origin` must equal one of the host's own origins as a
  whole string: its bound addresses (each loopback binding under `localhost`, `127.0.0.1` and
  `[::1]`) and, in Development, the web dev server (`http://localhost:4300`, `http://127.0.0.1:4300`).
- **Neither header** (curl, an agent, the .NET client, tests): passes unchanged.

Every guarded response carries `Vary: Sec-Fetch-Dest, Sec-Fetch-Mode, Sec-Fetch-Site`. The host has
no CORS policy, and must not gain one: the rule stops requests that do harm unread, and relies on
cross-origin reads staying refused. The dev server (`src/Read2Me.Web/dev.ts`) forwards the browser's
headers on plain HTTP and passes `Origin` and `Sec-Fetch-*` to its upstream hub socket, so the
host's one check covers dev too; it has no check of its own.

Every response, from any route, file mount, error page, redirect, 403 or 404, carries four headers
(`Api/SecurityHeaders.cs`, first in the pipeline and written as the response starts, so an exception
handler's `Response.Clear()` keeps them):

- `X-Frame-Options: DENY`: the app never frames itself, so no page may frame it (clickjacking).
- `X-Content-Type-Options: nosniff`: `/workspace` serves uploads typed by their extension.
- `Cross-Origin-Opener-Policy: same-origin`: a page that opened the app keeps no handle on it. A
  link out to another site must carry `rel="noopener"`, as the help panel's guide links do.
- `Cross-Origin-Resource-Policy: same-origin`: another page's `<img>`, `<audio>` or `<script>` of a
  host response fails, with request isolation's 403 or without it.

Not sent: `Referrer-Policy` (the browser default, `strict-origin-when-cross-origin`, already holds)
and `Permissions-Policy` (Chromium only). `frame-ancestors` belongs to the Content-Security-Policy.

The app's page (`/app` and every path under it, `/app/index.html` by name included) carries an
enforcing Content-Security-Policy with a fresh nonce on every response (`Api/AppPage.cs`):

```
script-src 'nonce-<n>' 'strict-dynamic'; object-src 'none'; base-uri 'self'; frame-ancestors 'none';
connect-src 'self'; require-trusted-types-for 'script'; trusted-types lit-html
```

- **Nonce**: 128 random bits, base64. The web build stamps `nonce="__CSP_NONCE__"` on every
  `<script>` and `<link rel="modulepreload">` of the built `index.html` (`src/Read2Me.Web/csp-nonce.ts`,
  since Bun's bundler writes those tags itself), and the host replaces that token. The host never
  finds tags by pattern. A new inline script in `index.html` gets the stamp too, so it needs no hash.
- **Trusted Types**: only lit-html's own policy may exist, so every HTML sink goes through a lit
  template (`unsafeHTML` included). A raw `innerHTML`, in app code or a dependency, throws.
- **`base-uri 'self'`**, not `'none'`: the page declares `<base href="/app/">` and the router
  resolves paths against `document.baseURI`. **`style-src` stays open**: lit templates carry
  `<style>` and `style=` bindings, and the theme injects a sheet.
- No `report-uri` or `Reporting-Endpoints`: nothing collects reports. The E2E base
  (`E2eTestBase`) fails any browser test whose page reports a CSP or Trusted Types violation.
- The not-built page has no policy (its one style is inline), and `bun run dev` serves the source
  page with none.

## Parts

Read the part for the area you are calling or changing:

- [settings.md](settings.md) — service configs, LLM chain, prompts, audio processing, themes, AI services, Preflight
- [setup-check.md](setup-check.md) — the Setup check: Quick, Full, re-run one check, no-Workspace mode
- [projects.md](projects.md) — create, import, reread, project metadata and cover, `X-Origin-Id`, status, checkpoints
- [series.md](series.md) — series, positions, posters, Series characters, shared Voices, differs/push/take
- [attribution.md](attribution.md) — character discovery, attribution queue, cast reads, bulk assign, release narration
- [voices.md](voices.md) — voice batches, one voice, reference audio, design prompts, overrides, voice editor, voice rules
- [audio.md](audio.md) — paragraph audio queue, chapter voices, audio reviews
- [pronunciations.md](pronunciations.md) — the Pronunciation list, impact, try-outs
- [book-commands.md](book-commands.md) — the `/commands` endpoint, Node insertion, narrator, delivery cues, titles, Edit with AI
- [assembly.md](assembly.md) — m4b assembly and outputs
- [live-hub.md](live-hub.md) — the `/hubs/live` SignalR methods and message families
- [history.md](history.md) — History steps, viewing a step (`?at=`), Restore

## Testing the API

Each behaviour is pinned once, at the lowest layer that can prove it. The host tests (`src/Read2Me.E2eTests/Tests/Api`) prove only what a host proves: routing, binding, DI wiring, status mapping, the wire shape and hub delivery.

- **A thin contract case per endpoint.** One success case, one case per distinct status mapping (404, 409, 422…), and the wire fields, shapes and `problem+json` bodies that matter. Counts, orderings, message texts and stored values belong to the unit test of the service that owns them. A refusal that many routes share (an unknown id, an unknown provider type) is asserted once, on one or two routes, not on every route.
- **The outer test loses an overlapping case.** When a host test and a unit test pin the same case, the host test goes, after you read the unit test's assertions and confirm they cover it. The exception is a unit test that only mirrors the code (NSubstitute fakes wired to match the implementation, a list of a schema's field keys, a test of a fake): that one goes, and the host test stays. If no unit test pins the case, the host case stays; write the unit test first when the case is a body of rules.
- **Migration tests only where a migration changes data.** A test that a table or column exists after `MigrateAsync` pins the model, not the migration. Keep a migration test when it seeds a row, migrates, and asserts the transform.
- **No wall-clock budgets in unit tests.** Never assert a duration or a percentile. To prove that something waits its turn, hold the lock it waits on and assert the order of events.
