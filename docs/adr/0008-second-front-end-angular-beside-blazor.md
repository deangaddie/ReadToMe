# Second front end (Angular) beside Blazor: hosting, live relay, coherence via receipts

## Status

accepted (2026-09-23; spike branch `spike/angular-frontend`, tickets 01–26 in `.scratch/angular-frontend/`)

## Context

ReadToMe's only UI was Blazor Server: every screen a stateful circuit, every status a
server-rendered component, every long-running job reported through `StatusDock` and a
web of presenters (`BookHierarchyPresenter`, `CharacterPresenter`, …) that hold mutable
copies of the book. That worked for one operator on one machine, but it made three things
hard: comparing UI frameworks on real workflows, letting an agent drive the app through a
plain HTTP surface, and reasoning about what a screen shows after a mutation (the presenter
had to know every follow-up rule itself — the problem ADR 0007 fixed on the server side).

The agent API (`docs/agents/api.md`) had meanwhile grown to cover every workflow, and ADR
0007 had made every book mutation answer with a receipt. That left the question: could a
second front end be a thin client over that API, and what would it need from the host?

## Decision

**A second front end (Angular) runs beside the Blazor UI on the same host.** Nothing in the
Blazor UI is removed or changed beyond host wiring; the comparison stays open until the
spike is judged on parity (`.scratch/angular-frontend/parity.md`).

**Hosting.** The .NET host serves the Blazor UI at `/` and, when `wwwroot/app/index.html`
exists, the Angular bundle at `/app` with SPA fallback and `--base-href /app/`. The .NET
build never touches the web project; a missing bundle yields a static "run `npm run build`"
page, never a Blazor fallback. Dev mode runs `ng serve` on `:4200` with a proxy to `:5000`
so the browser stays same-origin. `scripts/build-web.ps1` produces the bundle for CI and the
browser tests.

**Live relay.** One SignalR hub, `/hubs/live`, is the only push channel. The host-side
`LiveRelay` subscribes to the in-process event sources the Blazor UI already used
(queue snapshots, node/item status, receipts, assembly, voice batch, watchdog, service
status, preflight, LLM/audio streams, throughput, settings) and forwards each as an event
family with a `kind` discriminator; project-scoped families go to a per-project group,
firehose streams to opt-in stream groups, one-connection runs (preflight, book edit, LLM
test) to the caller's connection. Snapshot-shaped families are also answerable on demand
(`GetSnapshot`) so reconnecting clients catch up without replay. The hub carries no
commands: every mutation is an HTTP call to the same API an agent uses.

**Coherence via receipts.** The web client never patches a book from a command response.
It sends the command, ignores the body, and reloads the affected view from the `receipt`
the hub pushes (ADR 0007 extended to a second client). The same receipt reaches every
client of that project, so a mutation in one browser, in the Blazor UI, or from an agent
shows in all of them within the relay's debounce. Transient state (selection, expansion,
reader mode, playback) stays client-side and is never sent.

**Readiness in one place.** Every AI action in the web app calls `Preflight.ensureReady`,
which plans server-side (`/api/preflight/{task}/plan`) and, when needed, runs the start
sequence and renders its stages from the `preflight` family. No component decides
readiness on its own (a unit test greps for stubs).

**Testing.** Vitest per component/service; browser tests in the existing xUnit + Playwright
project against `/app/...` on the in-proc host with the same fakes and seeders as the Blazor
tests, gated on a built bundle. No second Playwright runner.

## Considered options

- **Replace Blazor outright.** Rejected: the point of the spike is a comparison on real
  workflows, and the Blazor UI is the only tested surface for several producer flows until
  parity is signed off.
- **Port the presenters to TypeScript.** Rejected: it would duplicate `BookViewProjection`
  logic and reintroduce the follow-up-rule sprawl ADR 0007 removed. The client is a view
  over API + receipts.
- **Per-feature hubs or SSE per page.** Rejected: one hub with families and groups is what
  the Blazor UI's event sources already map to, and one connection is what the connection
  dot and reconnect logic can reason about.
- **Client-applied optimistic updates.** Rejected: receipts arrive within the debounce
  window and the cost of a wrong optimistic patch (a view that disagrees with the host)
  is the bug class ADR 0007 exists to prevent.

## Consequences

- Two front ends must be kept in step by the API, not by shared components. A new
  producer function is an endpoint first, then a page in each UI that keeps parity.
- The hub contract is now a public surface: `live-messages.spec.ts` reads the C# sources
  and fails on drift, so families and `kind` strings change on both sides in one commit.
- Blazor-side gestures that previously only updated a presenter now also travel through the
  relay (e.g. `ObservedAiServiceControl` publishes status after every op) so the Angular
  chips follow Blazor-driven changes.
- The web bundle is a build artefact (git-ignored); anything that needs it (`/app`, the
  web browser tests) degrades explicitly when it is absent.
- Deciding the comparison is a separate decision; this ADR records how the two coexist,
  not which one wins.
