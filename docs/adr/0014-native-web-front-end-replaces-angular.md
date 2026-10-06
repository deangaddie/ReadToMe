# Native web front end replaces Angular: custom elements built with Bun

## Status

accepted (2026-10-06). Decided in the native-web wayfinder map (`.scratch/native-web/`, go/no-go ticket 06).
Amends [ADR 0012](0012-blazor-ui-removed.md): Angular stays the only UI until the native app takes over `/app`.

## Context

The Angular app (`src/Read2Me.Web`) became the only UI on 2026-10-01. It is on Angular/Material 22 and has not yet
been through an upgrade, so the churn cost is still ahead of it. Three motives, in this order, prompted the review:

1. Angular/Material upgrade churn and dependency weight.
2. Code an agent can navigate as plain platform code.
3. Owning the stack.

Research and two prototypes in Chromium and Firefox showed the following:

- **Cast page slice.** Light DOM with `@scope`, `use()`/`provide()` services, a Navigation-API router with an
  unsaved-changes guard, and native dialog, popover and anchor positioning. 18/18 checks passed. Ported components
  came out at about half their Angular line count. The main bundle is 30 KB gz, against Angular's 104 KB.
- **The two pieces with no platform equivalent.**
  - The variable-height virtual scroll: one element replaces the CDK viewport and its strategy at the same size, with
    exact `scrollToIndex` and the same anchoring.
  - The structure tree: the ARIA tree keyboard is written by hand.

  19/19 checks passed on real books.
- **Bun covers the whole toolchain on Windows.** Install, bundle, dev server (including the SignalR WebSocket),
  tests, with `tsc` and `openapi-typescript` alongside.

## Decision

**The front end moves off Angular to framework-free custom elements, built and tested with Bun.** It is one stack,
decided as one:

- **Toolchain.** Bun replaces Node/npm. `package.json` and the npm registry stay. `tsc --noEmit` and
  `openapi-typescript` stay.
- **Runtime dependencies.** `@microsoft/signalr`, `@preact/signals-core` (behind an Angular-shaped adapter) and
  lit-html. There is no framework, no CLI, and no compiler step beyond TypeScript stripping and bundling.
- **Browsers.** Evergreen Chromium and Firefox.
- **Conventions.** Those recorded in the native-web tickets 04 and 05.
- **The spike's code is the seed.** `core/` and `ui/` on `spike/native-web` (`src/Read2Me.Native`) are reviewed and
  carried into the real app. The spike's pages are throwaway.
- **Parity is what the E2E suite covers.** Anything else may be pruned screen by screen.
- **The host contract is unchanged.** `/app`, `/api`, `/hubs/live` and `/workspace` stay as they are. Until the
  cutover, ADR 0012 holds: Angular serves `/app`.
- **One reversal point.** The migration starts with the Book page, the hardest screen. After it, real cost and parity
  are compared with the prototypes. A screen far over its estimate, or one that loses E2E behaviour, sends the effort
  back to Angular. The thresholds are set in the migration spec.

## Considered options

- **No-go: stay on Angular and keep upgrading.** Rejected: it keeps the churn and the framework indirection the
  review set out to remove, and the prototypes found no blocker.
- **Defer until the first painful Angular major.** Rejected: the evidence is in hand now, and the app is young enough
  that moving it is cheaper today than after more screens grow on Angular.
- **Adopt Bun for the Angular app alone, as a separate decision.** Rejected: the Angular build tooling still assumes
  Node, so Bun only pays off once the CLI is gone.
- **Other frameworks (Svelte, Solid, Vue, Lit's `LitElement`).** Out of scope: each trades one framework's churn for
  another's.
- **`content-visibility: auto` instead of a virtual list.** Rejected: its jumps land up to 216 px off, it keeps every
  row in the DOM, and it has no anchoring when chapters are prepended.

## Consequences

- This is a second UI migration right after the Blazor removal, and the gains are mostly future ones. The reversal
  point exists for that reason.
- The cost is mostly mechanical:
  - about 6.7k template lines;
  - 44 TestBed specs to rewrite (the 43 pure specs port as is);
  - `inject()` → `use()` in 93 files;
  - 46 E2E locators that hit Material DOM.
- Two pieces are unproven and are named as risks in the migration spec: heavy reader rows inside the virtual list,
  and the tree's per-node menu.
- Element classes share `HTMLElement`'s namespace. Method names such as `remove` and `focus` collide with it.
