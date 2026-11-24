# Audit: motion and transitions against modern-web-guidance (#454)

Research ticket #454 under map #444. This file is the input to this area's triage (#455).

- **Source**: the bundled guide markdown under `~/.claude/skills/modern-web-guidance/guides/` (skill version `2026_11_10-47b4c80e`). No `npx`/CLI fetch was run. Browser support is quoted from each guide's own "Fallback strategies" data unless marked otherwise.
- **Code audited**: `src/Read2Me.Web` at `main` `13fc2cb2`. Line numbers are against that commit.
- **Bar for a finding** (map #444, Notes): it changes behaviour or robustness (accessibility, performance, Firefox fallbacks), not just newer syntax; it ships in Chromium and Firefox, or it has a fallback of about 20 lines or less with no dependency.

## What the app has today

There's very little motion. Every animation and transition in the app's CSS is listed here, from a grep for `animation|transition|@keyframes|prefers-reduced-motion|scroll-behavior`:

| Where | What | Reduced-motion handling |
|---|---|---|
| `styles/global.css:1053` `.r2m-spinner` | `r2m-spin` rotate, 0.8 s, infinite | none |
| `styles/global.css:1086` `progress:indeterminate` | `r2m-sweep` background-position, 1.2 s, infinite | none |
| `pages/projects/projects-page.css:211` skeleton | `projects-shimmer`, 1.4 s, infinite | `animation: none` under `reduce` (`:226`) |
| `shell/shell.css:238` `.shell__rail` | `width 150ms` (layout property) | none |
| `styles/global.css:2160` `.r2m-stream-disclosure__body` | `height 0 → 32vh`, 0.15 s | none |
| `styles/global.css:2192` `.r2m-expansion` chevron | `rotate 120ms` | none |
| `styles/global.css:332` switch knob | `translate 120ms` | none |
| `styles/global.css:1926` `.r2m-project-card__fill` | `width 200ms` | none |
| colour, opacity and shadow hovers (book.css, structure-tree.css, global.css `:321`, `:1624`, `:1697`, `:1740`, `:1814`, projects/themes cards) | 100–120 ms | none needed: the guide says to keep these ("Keep non-motion transitions … enabled") |

There's no `@starting-style`, no `transition-behavior`, no `overlay`, no view transitions, no `element.animate()`, no `transitionend`/`animationend` waits, no `scroll-behavior: smooth` and no `behavior: 'smooth'` call. `measured-list.ts:211` accepts a `ScrollBehavior`, but no caller passes one. Dialogs, popovers, the tooltip and toasts appear and disappear instantly.

## Findings: keep

### K1. Collapsed stream disclosure stays in the tab order and the accessibility tree

- **Guide**: `css/animate-to-intrinsic-sizes`: "MANDATORY Accessibility Synchronization: Ensure elements collapsed to zero dimensions are removed from the assistive technology tree, and sync aria-expanded states on triggers."
- **Where**: the Stream disclosure partial, `ui/stream-disclosure.ts:32`, with CSS at `styles/global.css:2157-2164`. It's used by Edit with AI, Discovery and Generate voice prompt.
- **What goes wrong today**: when the strip is closed, its body is only `height: 0; overflow: hidden`. The `<r2m-stream-llm>` inside stays mounted on purpose so it misses no events. Every turn it renders is a `<details open>` with focusable `<summary>` elements (`ui/stream-llm.ts:76`, `:92`, `:98`), plus the "Jump to latest" button (`:114`) when it's paused. So once a request has streamed, a keyboard user who tabs past "Show AI activity" lands on summaries they can't see: focus disappears inside a 0 px box. A screen reader also reads the whole hidden log. The toggle's `aria-expanded="false"` says the opposite of what assistive technology finds.
- **Fix**: set `inert` on the body while it's closed (`?inert=${!open}`). The element stays mounted, so it still captures every event, and it leaves the tab order and the accessibility tree. Add `aria-controls` on the toggle while you're there.
- **Firefox**: ships `inert` (Baseline since 2023). This compat fact isn't from these guides, which only use `inert` in `performance/faster-spa-view-transitions`. No fallback is needed.
- **Size**: S. **Shared primitive**: yes (Stream disclosure). One proof: Tab from the closed toggle must not reach a summary, in both browsers.
- **Verdict**: **keep**. This one is the most important: it's a real keyboard trap and screen-reader defect in three dialogs.

### K2. Spinner and indeterminate progress ignore reduced motion

- **Guide**: `ui-behaviors/motion`: "Always respect reduced motion preferences." It also says: "Use `@media (prefers-reduced-motion: reduce)` when the reduced-motion strategy is to **change animation parameters** (such as slowing down `animation-duration`)". Its own example is `progress:not([value]) { animation: slide 1s infinite linear; @media (prefers-reduced-motion: reduce) { animation-duration: 20s; } }`.
- **Where**: the Spinner (`styles/global.css:1053`) and the Progress bar's indeterminate sweep (`styles/global.css:1086`).
- **What goes wrong today**: both loop forever at full speed for users who've asked for reduced motion. They're on screen for minutes during AI runs (job pills and cards, preflight, the stream dialogs, every busy button), beside the content people are reading. They're the app's only persistent motion, and the skeleton shimmer is the only animation that handles the preference.
- **Fix**: add a nested `@media (prefers-reduced-motion: reduce)` to each that slows it down (for example spin 3 s, sweep 6 s) rather than removing it. A frozen ring or bar would lose the "busy" cue, and the guide warns "Keep the final state". Don't add a global `animation-duration: 0.01ms` (the guide says **DO NOT**).
- **Firefox**: media queries and the animations already ship in both. No fallback.
- **Size**: S (CSS only, two rules). **Shared primitive**: yes (Spinner, Progress bar).
- **Verdict**: **keep**.

### K3. Spatial transitions aren't gated on reduced motion, and the rail animates `width`

- **Guide**: `ui-behaviors/motion`: "only dampen or disable spatial movement, scaling, rotation, or persistent animations" and "Prefer to animate `opacity` and `transform` … to ensure animations stay on the compositor thread." Also: "Use `@media (prefers-reduced-motion: no-preference)` when the reduced-motion strategy is to **not have the animation at all**".
- **Where**:
  - `shell/shell.css:238`: the nav rail's `transition: width 150ms ease`. This is layout motion. On project pages it resizes the main pane on every frame, so `<r2m-measured-list>`'s per-row `ResizeObserver` re-measures each visible reader row on every frame of the slide.
  - `styles/global.css:2160`: Stream disclosure body `height` slide.
  - `styles/global.css:2192`: Expansion panel chevron `rotate`.
  - `styles/global.css:332`: Switch knob `translate` (small; include it for consistency or leave it, triage's call).
- **What goes wrong today**: users with reduced motion still get the sliding and rotating. The rail's `width` transition also costs a layout and reader re-measure per frame, where a compositor-only animation would cost none.
- **Fix**: move each `transition` into `@media (prefers-reduced-motion: no-preference) { … }`, nested in the rule, so the end state still applies instantly. Rewriting the rail to animate `transform` instead of `width` would change the layout model (the pane beside it has to resize anyway), so leave that out: it's L for a 150 ms effect. Gating already removes it for the users who asked.
- **Firefox**: ships. No fallback.
- **Size**: S (CSS only, four rules in two files). **Shared primitive**: yes (Expansion panel, Switch, Stream disclosure), plus the shell.
- **Verdict**: **keep**. It could ship in the same PR as K2 (same guide, same area), or K1 could absorb the disclosure part.

### K4. Auto-updating stream logs have no visible pause control

- **Guide**: `ui-behaviors/motion`: "**Provide a pause mechanism**: … Any moving, blinking, or scrolling content that starts automatically, lasts more than five seconds, and is shown alongside other content needs a visible control to pause, stop, or hide it, such as a `<button>` next to the content that works with the keyboard (WCAG 2.2.2 Pause, Stop, Hide). `prefers-reduced-motion` doesn't replace the control". Also: "If the button's label changes between "Pause" and "Play", don't also set `aria-pressed`".
- **Where**: `ui/autoscroll.ts` (shared by both streams); `ui/stream-llm.ts:112-121` and `ui/stream-audio.ts:57`, `:116-120` (the "Jump to latest" button). They show in the activity drawer's stream tabs, the LLM test console and the three AI dialogs' disclosures.
- **What goes wrong today**: while a run streams, the log scrolls itself for as long as the run lasts, which is often minutes. The only way to stop it is to scroll up with a wheel, touchpad or keys inside the scroller. Nothing visible says that, and the only control ("Jump to latest") appears after you've already paused. Keyboard and switch users have no obvious way to freeze the view and read a turn.
- **Fix**: show a persistent "Pause" / "Follow" button beside each stream, driving `Autoscroll.paused`. Change its label, not `aria-pressed`, as the guide says. "Jump to latest" can become that button's paused state, so it's one control rather than two.
- **Firefox**: plain DOM. Ships.
- **Size**: S–M. **Shared primitive**: yes (`Autoscroll`, LLM stream, Audio stream).
- **Verdict**: **keep**. The stream logs having no `role=log`/live region is an accessibility-area concern, not part of this finding.

### K5. Splitter has no single-pointer alternative to dragging (borderline)

- **Guide**: `ui-behaviors/drag`: "Ensure the draggable element can also be moved or operated using a single pointer without dragging (for example, clicks or taps)." This is WCAG 2.5.7 Dragging Movements, AA.
- **Where**: the Splitter, `ui/splitter.ts:130-150`.
- **What's already right**: it follows the rest of `drag` already. It drags only for the primary button (`:131`), uses `setPointerCapture` (`:137`), follows one `pointerId` (`:142`), and ends on `pointerup`, `pointercancel` and `lostpointercapture` (`:97-99`). It sets `touch-action: none` and `user-select: none` (`global.css`), and it has arrow, Home and End keys with a focusable `role=separator`.
- **What goes wrong today**: a pointer-only user who can't drag (tremor, head pointer, some touch setups) can only double-click to reset to the default width. They can't make a pane bigger or smaller.
- **Fix options**: clicking the focused gutter could step it like an arrow key, or a small menu could offer "Wider / Narrower / Reset". The guide lists "Floating panels: … 'Dock left', 'Move left' …" style menus as the pattern.
- **Firefox**: ships.
- **Size**: M (interaction design needed). **Shared primitive**: yes (Splitter).
- **Verdict**: **keep, low priority**. The guide's scope is "floating element[s]", and a resize separator only fits by analogy. 2.5.7 applies regardless. Triage may decide pane width is a convenience with a keyboard path and drop it.

## Findings: drop

| # | Guide | Finding | Reason dropped |
|---|---|---|---|
| D1 | `ui-behaviors/animate-to-from-top-layer`, `ui-behaviors/animate-element-entry-exit` | `<dialog>` (`openDialog`), menus, the tooltip popover and toasts have no entry or exit animation. | Not a fix: nothing animates today, so nothing is broken, and adding motion is polish. An exit animation would fail the browser gate. The guide's data: "Browser support for overlay: … Unsupported in: Firefox and Safari", and "transitionable_display … Unsupported in: Firefox" (Firefox bug 1882408). The documented fallback (`canTransitionDisplay` probe, `data-closing`, `closeTopLayer`, `cancel` routing) runs to about 40 lines. Even then "Popover light dismiss and `popovertarget` toggles cannot be intercepted … those exits are instant", and the menus are `popovertarget` driven. An entry-only `@starting-style` fade ships in both, but it's style-only. **Cross-area note** for the top-layer triage: if anyone does adopt it later, the guide's rules are `overlay` + `allow-discrete` as a separate longhand, and transforms only under `no-preference`. |
| D2 | `ui-behaviors/motion` | Skeleton shimmer (`projects-page.css:211`, `:226`) declares the animation, then cancels it with `animation: none` under `reduce`, where the guide prefers opting in under `no-preference`. | Behaviour is identical. Syntax-only. |
| D3 | `ui-behaviors/motion` (performance) | Project card progress fill animates `width` (`global.css:1926`) instead of `transform: scaleX`. | A 6 px bar that moves once per progress tick. The rewrite deletes no code and gains nothing measurable. |
| D4 | `ui-behaviors/same-document-transitions`, `directional-navigation-transitions`, `group-element-transitions`, `scoped-component-transitions` | The router (`core/router.ts`) and lists swap content with no view transition. | No surface for a fix: adding view transitions is a new feature. `scoped-component-transitions` is also "Supported by: Chrome 147 … Unsupported in: Firefox and Safari". |
| D5 | `performance/faster-spa-view-transitions` | `<r2m-outlet>` destroys the old page on navigation instead of caching it under `content-visibility: hidden`. | It's a performance feature with no measured problem. The routes are dynamic (`projects/:folder/...`), so the guide's own "MANDATORY: … implement an eviction strategy" applies, which is well past S. It also conflicts with the project shell's join/leave lifecycle (conventions 10). |
| D6 | `ui-behaviors/scroll-target-on-load` | The reader opens at a Book address by JS scroll through `<r2m-measured-list>`. | Already handled in JS. `scroll-initial-target` is "Supported by: Chrome 133 … Unsupported in: Firefox and Safari", and it can't target a virtualised row that isn't rendered yet. |
| D7 | `ui-behaviors/highlight-text-ranges` | The pronunciation pages wrap the matched word in `<mark>` (`pages/pronunciations/pronunciation-sentence.ts:123`, `pronunciation-try.ts:210`). The reader's `.r2m-pron-mark` spans (`pages/book/book.css:407`) are focusable controls. | Already right. The guide says that where "the highlight conveys meaning … use `<mark>` instead" of the Highlight API. The reader marks are interactive, so they need real elements. |
| D8 | `css/animate-to-intrinsic-sizes` | Expansion panels (`<details class="r2m-expansion">`) open and close with no height animation. | `interpolate-size` / `calc-size()`: "Unsupported in: Firefox and Safari". The guide calls them a progressive enhancement, and adding one is polish, not a fix. (The same guide's accessibility rule is K1.) |

## Guides with no surface in the app

- `ui-behaviors/dynamic-sibling-animations`: no staggered list animation.
- `ui-behaviors/interactive-content-reveal`: no pointer-driven mask reveals.
- `ui-behaviors/drag-and-drop`: no reorderable list. `<r2m-file-drop>` takes files from the OS. It doesn't reorder.
- `ui-behaviors/motion`'s clip-path/mask focus-outline rule: no `clip-path` or `mask-image` in the app's CSS.
- `ui-behaviors/motion`'s animation-event rule: no code waits on `animationend`/`transitionend`, so the "never fire under reduced motion" trap can't happen today.
- View-transition guides (D4) and `faster-spa-view-transitions` (D5): no view transitions or view caching.

## Outside these guides, noticed in passing

- `ui/file-drop.ts:90`, `:97`: `dragleave` clears `--over` whenever the pointer crosses onto a child (the icon, label or button), and the next `dragover` sets it again, so the highlight may flicker during a drag. I didn't confirm this in a browser. The usual fix is a depth counter over `dragenter`/`dragleave` or checking `relatedTarget`. This belongs in triage for the forms or behaviour area, if anywhere.

## Summary for triage

- **Keep (5)**: K1 (stream disclosure `inert`, S), K2 (spinner and progress slowed under reduced motion, S), K3 (spatial transitions gated on reduced motion, S), K4 (stream pause control, S–M), K5 (splitter single-pointer alternative, M, borderline).
- **Drop (8)**: D1–D8.
- All five keeps ship in Chromium and Firefox with no fallback needed. All five touch a shared primitive. K2 and K3 are one guide (`motion`, reduced motion) and could be one PR.
