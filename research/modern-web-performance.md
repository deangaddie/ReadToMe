# Audit: performance (INP, long tasks, rendering long content) against modern-web-guidance

Research ticket #456, parent map #444. Audited 2026-11-24 against `origin/main` at `13fc2cb2`.

**Source**: the guide markdown bundled with the `modern-web-guidance` skill (`~/.claude/skills/modern-web-guidance/guides/`, skill version `2026_11_10-47b4c80e`), read locally. No `npx … retrieve` was run. Each finding quotes its guide. Its browser data is the guide's own "Fallback strategies" block unless the finding says otherwise. Code references are to `src/Read2Me.Web/src/`.

**The map's rules applied** (#444 Notes): a finding counts only when it changes behaviour or robustness. It must ship in Chromium and Firefox, or have a fallback of about 20 lines or less with no dependency. Anything that only swaps in newer syntax is dropped.

**Context that shapes the verdicts**: the app is served from the local host (`localhost:5000`). The page, its chunks, fonts, images and `/workspace` audio all come over loopback. So the guides' **network-priority** advice (`fetchpriority`, preloads, `priority: 'low'`) has almost nothing to win here. Main-thread and rendering cost is a different matter: the guides' **INP and rendering** advice applies in full. The heavy surfaces are the LLM stream, which takes 100 ms token batches for minutes at a time, the virtualised reader, and the project grid.

## Summary

| # | Guide | Where | Verdict | Size | Shared primitive |
|---|---|---|---|---|---|
| 1 | `interactions-in-complex-layouts` / `defer-rendering-heavy-content` | LLM stream: each delta writes, then `Autoscroll` reads `scrollHeight` over up to 50 turn cards | **keep** | S | yes (LLM stream, `autoscroll.ts`) |
| 2 | `defer-rendering-heavy-content` (`content-visibility: hidden`) | Stream disclosure's collapsed body stays laid out and reachable by Tab | **keep** | S | yes (Stream disclosure) |
| 3 | `performance` (lazy-load below-the-fold images) | Project-card and series-poster covers load eagerly at full size | **keep** | S | yes (Project card) |
| 4 | `lazy-load-video-and-audio` | Every `<r2m-audio-player>` sends `preload="metadata"` the moment it renders, including every row in the reader's ±800 px buffer | **keep** (low) | S | yes (Audio player) |
| 5 | `performance` (layout thrashing) | `MeasuredList.update()` reads layout after writing it, twice per scroll event | **keep** (low) | S | yes (Measured list) |
| 6 | `break-up-long-tasks` / `schedule-tasks-by-priority` | LLM stream re-folds every buffered event per delta | drop | — | — |
| 7 | `defer-rendering-heavy-content` | Book reader | drop (already done) | — | — |
| 8 | `defer-work-until-scroll-ends` | Book page's 250 ms edge poll | drop | — | — |
| 9 | `performance` (recurring timers) | Activity elapsed ticker | drop | — | — |
| 10 | `interactions-in-complex-layouts` | Structure tree re-render on status deltas | drop (no evidence) | — | — |
| 11 | `performance` (code splitting) | Routes | drop (already done) | — | — |
| 12 | `performance` / `optimize-preload-priority` (font preload) | `global.css` `@font-face` | drop | — | — |
| 13 | `optimize-image-priority` / `optimize-script-priority` / `optimize-preload-priority` / `deprioritize-background-fetches` | the whole app | drop (no contention on loopback) | — | — |
| 14 | `identify-inp-causes` / `interaction-paint-timing` / `detect-initial-visibility-state` | RUM | drop (no surface) | — | — |
| 15 | `js` (Temporal guides) | `history/history-rows.ts` day labels | drop (works today) | — | — |

**Kept: 5. Dropped: 10.** Findings 1 and 2 touch the same two files, so one PR could take both. A separate note at the end flags a cost the guides don't cover.

---

## Kept

### 1. LLM stream forces a full layout of every turn card on each 100 ms delta

- **Guide**: `interactions-in-complex-layouts`: *"By applying `content-visibility: auto` to self-contained layout regions … you can isolate rendering work."* Also `defer-rendering-heavy-content`: *"DO target large, self-contained layout blocks that are strictly below the initial fold"* and *"You MUST pair `content-visibility: auto` with `contain-intrinsic-size`."* The forced layout itself breaks `performance`: *"DON'T cause layout thrashing: Avoid interleaving DOM reads … and writes … Batch DOM reads, then batch DOM writes."*
- **Where**:
  - `app/ui/stream-llm.ts:59-61`: `updated()` calls `Autoscroll.afterRender()`.
  - `app/ui/autoscroll.ts:34-37`: `el.scrollTop = el.scrollHeight`, a layout read straight after lit's DOM write.
  - The turn card is `.r2m-stream-llm__turn` (`styles/global.css:1395`). Each card holds the full prompt in a `<pre>` (`stream-llm.ts:94`) plus thinking and response. The feed keeps 50 turns (`activity/stream-feed.ts:16`).
- **What goes wrong**: `docs/agents/api/live-hub.md` says *"LLM `Delta` messages are 100 ms batches"*. So while attribution runs, the stream re-renders about 10 times a second for minutes at a time. Each render dirties the open turn's `<pre>`. The `scrollHeight` read then forces style and layout of the whole scroll container: all 50 cards, each with a multi-KB prompt `<pre>`, though only the last one or two are on screen. This is the frame work that pushes up presentation delay. While the activity drawer's LLM tab or an AI dialog's stream is open, clicks and typing elsewhere wait behind it. Firefox has no Long Animation Frames API, so the cost can't be measured there. It has the same layout model, so the cost is the same.
- **Fix shape**: add `content-visibility: auto; contain-intrinsic-size: auto none auto <estimate>` to `.r2m-stream-llm__turn`. Off-screen cards then skip layout, and the `scrollHeight` read only lays out the visible ones (`auto` remembers each card's real size once rendered). The guide's own warning still applies: *"MANDATORY Accessibility Verification: … verify sequential keyboard reachability"*, because the cards hold focusable `<summary>` elements.
- **Browsers**: *"Supported by: Chrome 108 …, Firefox 130 (Sep 2024)"*. It ships in both, so no fallback is needed: *"The property degrades gracefully."*
- **Size**: S (CSS only, plus the proof). Shared primitive: **yes**. The LLM stream is used by the activity drawer, the stream disclosure (Edit with AI, Generate voice prompt, Discover) and the LLM test console. The LLM stream row in `primitives.md` gets one clause.
- **Verdict**: **keep**. Of the five, this one has the most user-visible effect. It also applies to `r2m-stream-audio` (`.r2m-stream-audio__scroll` shares the rule at `global.css:1360`). That stream is lighter: no prompt `<pre>`, and one event per phase, not per 100 ms.

### 2. The collapsed stream disclosure is still laid out and still reachable by Tab

- **Guide**: `defer-rendering-heavy-content`: *"**Toggle State** (Fast view switching) | **`content-visibility: hidden`** | Skips layout calculations for hidden divs but preserves style containment state, allowing for instantaneous toggling"*. Also: *"`content-visibility: hidden` excludes the element and its children from the accessibility tree and find-in-page search"*. `efficient-background-processing` says the same about not doing rendering work for content the browser isn't showing.
- **Where**: `app/ui/stream-disclosure.ts:32-34` mounts `<r2m-stream-llm>` all the time, on purpose (*"so it captures every request made while the dialog is open"*). It is collapsed only by `height: 0; overflow: hidden` (`styles/global.css:2157-2164`).
- **What goes wrong**:
  1. **Performance**: with the strip closed, every delta still renders, lays out and autoscrolls the hidden stream (finding 1's cost), in the three AI dialogs where the user is typing.
  2. **Robustness / accessibility**: `height: 0; overflow: hidden` neither hides the content from assistive technology nor takes it out of the tab order. Each turn's `<summary>` elements stay focusable while invisible. A keyboard user tabbing past "Show AI activity" lands on controls they can't see. A screen reader reads turns the user collapsed.
- **Fix shape**: add `content-visibility: hidden` to `.r2m-stream-disclosure__body` and `visible` to `--open`. The element stays mounted, so no event is missed (the stated reason for always mounting), and the guide says the rendering state is kept for a fast reopen. One cost: the 0.15 s height transition hides the contents as soon as it starts collapsing, not when it ends. A test should check that the "Jump to latest" state and autoscroll still land at the bottom on reopen.
- **Browsers**: `content-visibility` ships in Firefox 130, so no fallback is needed for the target. The guide's `@supports` fallback (`display: none` until supported) is only for older browsers.
- **Size**: S. Shared primitive: **yes** (Stream disclosure row).
- **Verdict**: **keep**. **Overlaps the accessibility area**: the focus and AT half is an accessibility fix. Per the map's "Cross-area overlaps", one triage should own it. Performance is the better fit, since the CSS change is the same one that saves the work.

### 3. Project and series covers load eagerly, at full size, for every card

- **Guide**: `performance`: *"DO utilize `loading="lazy"` on all below-the-fold images"* and *"DO use `loading="lazy"` to avoid loading competing elements that are far out of the initial viewport"*. Also `optimize-image-priority`: *"For standard below-the-fold images, `loading="lazy"` is sufficient."*
- **Where**:
  - `app/ui/project-card.ts:63-67`: `<img class="r2m-project-card__image" src=${coverUrl} …>` with no `loading`.
  - `app/pages/projects/series-card.ts:41-45`: the same for series posters.
  - The cards sit in `.projects__grid` (`pages/projects/projects-page.css:187`, `minmax(180px, 1fr)`).
- **What goes wrong**: the projects page fetches and decodes the cover of every project at once. A cover is the workspace's own image, usually an EPUB cover of 1–2 MP, shown about 180 px wide. With a library of dozens of books, opening the page decodes all of them before the user scrolls, holding memory and main-thread decode time that competes with the first interaction. Fetching over loopback is cheap. Decoding is not.
- **Fix shape**: add `loading="lazy"` on both `<img>`s. Layout is already reserved by `aspect-ratio: 3 / 4` on `.r2m-project-card__cover` (`global.css:1828`), so this meets the guide's *"DON'T delete necessary dimensions"* without `width`/`height` attributes. The first row of cards is in view, and lazy images in view load at once. The guide's *"DON'T lazy load above-the-fold images"* matters only for an LCP image, and this app has no LCP-critical image. Leave `project-details-panel.ts:72` (one cover, above the fold) as is. Server-side thumbnails, `srcset`, `sizes` and `<picture>` would need new host endpoints, which is out of this map's scope.
- **Browsers**: the bundled guides give no compatibility block for `loading` on `<img>`. Outside the guides: Firefox has shipped it since 75 (2020) and Chromium since 77, so it ships in both. Check this against MDN when ticketing.
- **Size**: S. Shared primitive: **yes** (Project card), plus the series card (a page partial).
- **Verdict**: **keep**.

### 4. Every audio player fetches metadata on render, including off-screen reader rows

- **Guide**: `lazy-load-video-and-audio`: *"`preload="metadata"` still initiates range requests for media headers across every player on the page"* and *"Add `loading="lazy"` to offscreen `<video>` and `<audio>` elements"*.
- **Where**: `app/ui/audio-player.ts:148-149`: `<audio preload="metadata" src=…>` on every player. These render in:
  - Audio mode in the book reader: one per item row (`pages/book/item-row.ts:252`). The measured list renders ±800 px past the viewport (`ui/measured-list.ts:9`) and re-creates rows as they scroll back in.
  - One per voice card on the Cast page (`pages/cast/voices/voice-card.ts:399, 483`).
- **What goes wrong**: scrolling a chapter in Audio mode creates and destroys dozens of `<audio>` elements. Each one opens a `/workspace/…` range request and a media pipeline to read a duration the reader may never look at. Opening the Cast voices list does the same for each voice. Loopback keeps each request cheap. The cost is the churn: per-element decoder setup on the main thread and host file I/O during a scroll.
- **Browsers**: *"Supported by: Chrome 150 (Jun 2026) and Edge 150 … Unsupported in: Firefox and Safari."* So `loading="lazy"` alone fails the gate. The guide's fallback: *"Pairing `loading="lazy"` with `preload="none"` or `preload="metadata"` ensures unsupported browsers still avoid downloading the full media file"*. In Firefox that changes nothing, because the app already uses `metadata`. Its `IntersectionObserver` fallback is about 50 lines, written for posters and `<source>` swaps. A cut-down version for this one element fits the ~20-line gate: start `preload="none"`, then switch to `metadata` once an `IntersectionObserver` (`rootMargin: 0`) sees the player. It would sit inside `AudioPlayer.connected()` and serve both browsers, with no native attribute needed.
- **Size**: S (~15 lines in one element). Shared primitive: **yes** (Audio player).
- **Verdict**: **keep, low priority**. The cost is real but small on loopback. Triage should weigh one thing: a player scrolled into view shows `--:--` for a moment longer before its duration arrives.

### 5. `MeasuredList.update()` writes styles and then reads layout, twice per scroll event

- **Guide**: `performance`: *"DON'T cause layout thrashing: Avoid interleaving DOM reads (`offsetHeight`, `getBoundingClientRect`) and writes (`style.height`) within the same loop. Batch DOM reads, then batch DOM writes."*
- **Where**: `app/ui/measured-list.ts:305-325`, run from the scroll listener (`:101-112`):
  1. `applyTotalSize()` writes `spacer.style.height` (`:302`).
  2. It then reads `this.scrollTop` and `this.clientHeight` (`:310, :314`), which forces style and layout.
  3. It writes `content.style.top` (`:317`).
  4. `scrollOffsetIntended()` reads `this.scrollTop` again (`:320`, via `:249`), forcing a second flush.
- **What goes wrong**: two forced synchronous layouts per scroll event in the Book reader and Character lines. `contain: strict` on the host (`:23`) keeps each one inside the list, so the cost is bounded. It still lands on the scroll frame, and rows are rendered `flow-root` paragraphs with chips.
- **Fix shape**: read `scrollTop`/`clientHeight` once at the top of `update()`, then do both writes. Pass the read offset into `topRow`. A one-function refactor, guarded by the existing `measured-list.spec.ts` and `reader-anchoring.spec.ts`.
- **Browsers**: no new API.
- **Size**: S. Shared primitive: **yes** (Measured list). This is the primitive with the most delicate anchoring rules. Its comments record fractional-offset and rounding fixes, so the refactor must keep `intendedTop` semantics exactly.
- **Verdict**: **keep, low priority**. It's a pattern fix whose gain is unmeasured. Triage may decide the risk to the anchoring outweighs it.

---

## Dropped

### 6. LLM stream re-folds every buffered event per delta

`break-up-long-tasks`: *"Any JavaScript execution exceeding 50ms should be split"*, with `scheduler.yield()` (*"Firefox 142 (Aug 2025)"*, so it ships in both).

On each 100 ms delta, `StreamFeed.receive` copies the whole event array (`activity/stream-events.ts:113`, `appendCapped`), and `foldLlmTurns` rebuilds every turn from scratch (`ui/llm-turns.ts:46-118`). That is O(events) per delta, with up to 50 turns of 100 ms batches. It's wasteful, but plain array work over a few thousand small objects is far below the guide's 50 ms threshold. Yielding inside a fold that has to finish before render would not help. The fix would be an incremental fold (append to the open turn), which is a design change, not a guide pattern. **Drop**: no evidence of a long task. Revisit if a Chromium trace of an attribution run shows the fold in a long animation frame.

### 7. Book reader: `content-visibility` for long chapters

`defer-rendering-heavy-content` recommends `content-visibility: auto` for long content. The reader already does better: `<r2m-measured-list>` virtualises rows (only the viewport ±800 px is in the DOM, `ui/measured-list.ts:9`), with `contain: strict` on the host (`:23`), and `BookStore` holds only a window of adjacent chapters. **Drop**: already done. A side note for whoever owns accessibility: virtualising costs find-in-page and AT reach over unrendered rows. The guide's alternative keeps the DOM for both, but a switch would be L and would undo the anchoring design. That makes it a feature decision, not a fix.

### 8. Book page edge poll (`setInterval` 250 ms)

`defer-work-until-scroll-ends`: *"DO use `scrollend` instead of debounced `scroll` events"* (*"Firefox 109"*). `performance`: *"DON'T block the thread with recurring timers: Avoid heavy polling with `setInterval`"*.

`pages/book/book-page.ts:260` polls `ReaderWindow.tick()` 4 times a second. Its doc comment (`reader-window.ts:79-83`) says why: *"a chapter shorter than the viewport never scrolls, and wheel input at the very top fires no scroll event at all"*. Neither case fires `scrollend`, so the guide's pattern cannot replace the poll. Each tick is a handful of offset reads. It isn't "heavy polling", and background tabs throttle it. **Drop**: the guide's pattern doesn't cover the reason for the poll.

### 9. Activity elapsed ticker (`setInterval` 1 s)

`activity/activity-store.ts:206` runs only while the queue is busy (`:200-201`) and sets one signal. **Drop**: not heavy, and already stops when idle.

### 10. Structure tree re-renders on each status delta

`interactions-in-complex-layouts`. `pages/book/structure-tree.ts:152-` re-runs the whole tree template (every visible node) when `nodeStatus` deltas arrive. The host debounces these to 250 ms (`live-hub.md`). lit's `repeat` then diffs per row, and the node menu is one shared element (`node-menu.ts`), not one per row. Even a very long book's tree is a few hundred small rows. **Drop**: no evidence. `content-visibility` on rows that small adds overhead for little gain. Revisit only with a trace on a long book.

### 11. Route code splitting

`performance`: *"DO use dynamic imports: Split routes"*. Every leaf route already lazy-loads its chunk (`app/app.routes.ts:19, 39, 46, 52, …`), and `build.ts:18` builds with `splitting: true`. `main.ts:26` lazy-imports the shell after the theme and the setup check. **Drop**: already done.

### 12. Font preload

`performance`: *"DO preload critical fonts"*. `styles/global.css:5-26` declares Inter and the icon subset. The icon font uses `font-display: block`, so icon-only buttons stay blank until it loads. The font URLs are content-hashed by `Bun.build` (`build.ts:22`), so a `<link rel=preload>` in `index.html` would need a build step to rewrite it. Over loopback the CSS → font chain costs a few milliseconds. **Drop**: no measurable gain on a local host for build-pipeline work.

### 13. `fetchpriority`, preload priority, script priority, `priority: 'low'` fetches

`optimize-image-priority`, `optimize-script-priority`, `optimize-preload-priority`, `deprioritize-background-fetches` all ship in both browsers (*"Firefox 132 (Oct 2024)"*). Each one shares bandwidth between competing requests. There is one module script (`index.html:11`, deferred by `type="module"`), no LCP-critical image, no preloads, no analytics or prefetch traffic, and every request goes to loopback. **Drop**: nothing competes for bandwidth.

### 14. RUM: INP attribution, interaction paint timing, initial visibility

`identify-inp-causes` (*"DO use the `web-vitals` library if no other RUM solution is in place"*, *"DO beacon back the required information to an analytics service"*), `interaction-paint-timing` (*"Supported by: Chrome 151 … Unsupported in: Firefox"*) and `detect-initial-visibility-state` (*"Unsupported in: Firefox and Safari"*) all feed field analytics. The app has no analytics endpoint. `web-vitals` would be a new runtime dependency (ADR 0014), and the map puts privacy and analytics out of scope. **Drop**: no surface. They're still useful by hand: a DevTools Performance trace of an attribution run, with the stream open, is how to confirm findings 1, 5 and 6 before or after the fix.

### 15. Temporal for history day labels

`calculate-event-differentials` / `coordinate-global-events` recommend `Temporal` for calendar-day differences (*"Chrome 144 …, and Firefox 139"*, so it ships in both). `history/history-rows.ts:109-118` works out "Today / Yesterday / weekday" from local midnights with `Math.round(… / 86_400_000)`. The rounding absorbs the 23 h and 25 h DST days, so the labels are already correct. **Drop**: works today, and the swap would only be newer syntax.

---

## Guides read with no surface in the app

- `lazy-load-video-and-audio` (video half): the app has no `<video>`.
- `deliver-optimized-decorative-images`: the app has no CSS background images. Cover placeholders are generated gradients (`placeholderGradient`).
- `conditional-async-dependencies`: nothing is polyfilled. `main.ts` already uses top-level `await` from the single entry point, which is the pattern the guide calls safe.
- `efficient-background-processing`: the app has no canvas, WebGL or animation loop. Its rule (don't render what the browser isn't showing) is applied to the stream disclosure in finding 2.
- `interaction-paint-timing`, `detect-initial-visibility-state`, `identify-inp-causes`: field measurement only (finding 14).
- `js/js.md`: its performance-relevant rules (`replaceChildren`, no `innerHTML = ''`) have no surface, because lit owns the DOM and the one `unsafeHTML` is the build-rendered help. Its array and promise rules are already ticketed off-map from `legacy-scan` (#445). `structuredClone`, `URLSearchParams`, `TextDecoder`, `AbortSignal.any/timeout`: no matching legacy pattern was found in `src/app` by grep.
- `js/capture-location-agnostic-data`, `js/sort-high-precision-timestamps`, `js/support-global-calendar-systems`: no plain dates, nanosecond timestamps or non-Gregorian calendars. Instants come from the host as ISO strings and are only shown.
- `js/calculate-event-differentials`, `js/coordinate-global-events`: see finding 15.

## Outside the guides (for triage to note, not a guide finding)

`history/history-rows.ts:66, 95-96, 112-121` calls `Date#toLocaleDateString` / `toLocaleTimeString` with options for every step on every History sheet render (`history-sheet.ts:82`). Each call with options builds a new `Intl.DateTimeFormat`. Building one formatter per call is a known slow path. Two module-level `Intl.DateTimeFormat` instances would remove it. No bundled guide covers this, so it fails the map's "guide id" requirement. It only matters if a project's History grows to hundreds of steps.
