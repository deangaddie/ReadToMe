# Blazor UI removed: the host is API + hub + the Angular app

## Status

accepted (2026-10-01). Decided in the Blazor removal spec (`.scratch/completed/blazor-removal/`, tickets 01–06).
Amends [ADR 0007](0007-book-mutations-reconcile-book-view-projections.md); supersedes the "Blazor stays" parts of
[ADR 0008](0008-second-front-end-angular-beside-blazor.md) and [ADR 0009](0009-angular-is-the-default-ui.md).

## Context

ADR 0009 made the Angular app the default UI and left Blazor running at `/blazor` until it was removed. The Angular
app had reached parity, and no new work went into Blazor. Keeping it cost a second E2E base, MudBlazor in two
projects, Razor Pages in the host, the circuit-scoped Book View projection, and comments and docs that explained
behaviour by pointing at Blazor.

## Decision

**Angular is the only UI.** The host serves the agent API, the live hub `/hubs/live`, `/workspace`, the audio
preview routes, and the Angular bundle at `/app` with its SPA fallback. `/` redirects to `/app/`; without a built
bundle `/app` serves the "run `npm run build`" page. Every old Blazor route (`/blazor`, `/project/{folder}`,
`/llm-settings`, the other settings pages, `/_blazor`, `blazor.server.js`, `_content/MudBlazor`) answers 404. There
are no redirects.

**ADR 0007 keeps its write side and loses its read side.** `BookMutations`, mutation implementations, receipts and
per-project serialization stay as decided. `BookViewProjection`, `BookViewSnapshot` and the presenter adapter are
gone: they were circuit-scoped and only Blazor used them. Receipts now have exactly two consumers, `BookMutations`
(which publishes them) and `LiveRelay` (which pushes them to the hub). Each client reloads the affected view from the
receipt, as ADR 0008 decided for Angular. The architecture tests in `OneConsistencyModelTests` enforce that consumer
list.

**Nothing is ported.** A behaviour that only Blazor had is dropped. Before deletion, a coverage audit gave every
Blazor test's behaviour an Angular, API or unit test, or recorded it as dropped (ticket 02).

**MudBlazor and Razor Pages leave the solution.** Outside Development, the unhandled-exception handler returns ProblemDetails. The stored
theme and palette shape is unchanged, including its MudBlazor-flavoured names.

## Considered options

- **Keep Blazor frozen at `/blazor` indefinitely.** Rejected: it gets no features, yet every mutation path still had
  to stay correct for its projection, and its tests ran on every build.
- **Redirect old Blazor URLs to their Angular routes.** Rejected: the app runs locally for one operator, and nothing
  links to the old routes.
- **Port Blazor-only behaviours first.** Rejected: the audit found four, all tied to Blazor mechanics Angular does not
  have (an armed bulk-assign mode, the projection's stale banner, a combined-view narration chip, a name prefix on a
  linked narrator's voice line).

## Consequences

- There is one E2E base, `E2eTestBase`, and the browser tests drive `/app/...` only.
- The verify skill and `tools/browse` drive the Angular app only.
- New UI work and its docs need no parity step; "both UIs" no longer exists.
- The EF migration history keeps its names. ADRs 0003 and 0007–0009 and `.scratch/completed/*` still describe
  Blazor as it was.
