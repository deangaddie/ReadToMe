# The Angular web app is the default UI; Blazor stays until removed

## Status

accepted (2026-09-24). Amends the hosting part of [ADR 0008](0008-second-front-end-angular-beside-blazor.md).

## Context

The Angular spike (ADR 0008, `.scratch/angular-frontend/`) reached parity with the Blazor UI
across all 26 tickets. The comparison that ADR 0008 left open has been decided: the Angular app
stays and Blazor goes. Removing Blazor is separate, later work.

## Decision

**`/` redirects to `/app/`.** Opening the host with no path lands in the Angular app.

**Blazor's home page moves to `/blazor`.** It is the only Blazor route that clashed with the
redirect. Its other routes (`/project/{folder}`, `/llm-settings`, …) keep their root-level paths
until Blazor is removed. The Blazor nav's "Projects" link points at `/blazor`.

## Consequences

- New UI work goes into `src/Read2Me.Web` only. The Blazor UI is kept working but gets no new features.
- A host without a built bundle redirects `/` to the "run `npm run build`" page, which links to `/blazor`.
- Removing Blazor later means dropping `/blazor`, `_Host`, and the Blazor E2E tests. `/` and `/app` stay as they are.
