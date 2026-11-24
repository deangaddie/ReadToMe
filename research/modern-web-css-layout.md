# Audit: CSS and layout against modern-web-guidance (#452)

Research ticket [#452](https://github.com/deangaddie/ReadToMe/issues/452), map [#444](https://github.com/deangaddie/ReadToMe/issues/444). Input to the triage in [#453](https://github.com/deangaddie/ReadToMe/issues/453).

- **Source**: the guide markdown bundled with the `modern-web-guidance` skill (version `2026_11_10-47b4c80e`), read from `~/.claude/skills/modern-web-guidance/guides/`. The CLI was not run. Each quote below is verbatim from the guide it names, and each compatibility line comes from that guide's own "Fallback strategies" data.
- **Code**: `src/Read2Me.Web` at `origin/main` `13fc2cb2`. Paths are relative to `src/Read2Me.Web/src/`. Primitives are named as in `docs/agents/web/primitives.md`.
- **Filter** (map Notes): a finding is kept only if it changes behaviour or robustness, and only if the fix ships in Chromium and Firefox or has a fallback of about 20 lines with no dependency.
- **Method**: I read the CSS and the templates. Nothing was measured in a browser. The widths in K1 and K2 are worked out from the CSS tokens, not observed, so the throwaway proof in each PR should confirm them.

Guides read: every file in `css/` (18), `ui-atoms/` responsive-table, single-line-overflow, scrollability-affordance-hints, state-aware-sticky-headers, scroll-position-aware-elements, component-specific-light-dark-theme, `ui-behaviors/` sortable-table and dynamic-sort, and every file in `user-experience/` (3).

## Summary

| # | Finding | Guide | Size | Shared primitive | Firefox |
|---|---|---|---|---|---|
| K1 | Page layouts break when the shell docks panels beside the page, because they use viewport media queries | css-layout, form-factor-adaptation | M | yes (shell, Master–detail) | ships |
| K2 | Dialogs overflow the viewport below about 370 px (400 % zoom), with their action buttons cut off | form-factor-adaptation | S | yes (Dialog) | ships |
| K3 | The collapsed "Show AI activity" body stays in the tab order and the accessibility tree | calculate-with-intrinsic-sizes | S | yes (Stream disclosure) | ships |
| K4 | Hover-revealed Node menu triggers are invisible on touch devices | natural-interactions | S | partly (Inline edit) | ships |
| K5 | Truncated single-line text has no way to read it in full | single-line-overflow | S | yes (Speaker menu, Inline edit) | ships |
| K6 | Focus indicators removed with a weak replacement, or one that vanishes in forced colours | natural-interactions | S | yes (Inline edit, Speaker menu) | ships |
| K7 | Reader and tree checkboxes (13 px) and the compact speaker chip (20 px) are below the 24 px target size, with no touch sizing | form-factor-adaptation | M | yes (Speaker chip) | ships |
| K8 | `order: 1` puts the checkpoint pills out of DOM order below 1100 px | css-layout | S | yes (Pipeline) | n/a |
| K9 | Scrolling tables have no keyboard-scrollable named region, and most tables have no accessible name | responsive-table, form-factor-adaptation | S | no | ships |
| K10 | The Activity bar's pill row hides its scrollbar and gives no scroll cue | scrollability-affordance-hints | S | no | ships (Firefox 159) |
| K11 | Cast sort: nothing is announced, and names with numbers sort as text | dynamic-sort, sortable-table | S | no | ships |
| K12 | Full-screen dialog and bottom sheet are sized in `vh`, not `dvh` | responsive-design, css-layout | S | yes (Dialog) | ships |
| K13 | The main scroller has no `scrollbar-gutter`, and nested scrollers chain into it | css-layout | S | yes (shell) | ships |

13 keep, 15 drop (listed after the keeps).

The three that matter most:

1. **K1**: with the rail expanded and the Activity drawer or History sheet open, the settings master–detail, the Cast page and the Overview keep their wide layouts in a page that is now too narrow.
2. **K2**: at 320 CSS px (a 1280 px screen at 400 % zoom, WCAG 1.4.10), every `openDialog` dialog is wider than the viewport. The right-aligned Confirm/Save button sits off-screen, and a modal can't be scrolled to it.
3. **K3**: Edit with AI, Generate voice prompt and Discover keep the hidden LLM stream's `<summary>` elements and its "Jump to latest" button in the Tab order while the disclosure is collapsed.

## Keep

### K1. Page layouts react to the viewport, not to the page's own width

- **Guide**: `css-layout` §4: "Query the size (or computed style) of an ancestor container rather than the viewport. Mental model: container queries = component context; media queries = global page layout and user preferences". `form-factor-adaptation`: "Use container queries instead of viewport-width media queries for component layout, so a component adapts wherever it is placed."
- **Where**: the shell docks the rail (`--r2m-rail-expanded: 240px`), the Activity drawer (`--r2m-activity-drawer-width: 420px`) and the History sheet (380 px) beside `.shell__main` at 900 px and wider (`app/shell/shell.css:306-333`). Every page breakpoint inside it measures the viewport:
  - Master–detail: `styles/global.css:2466-2475` (320 px list, stacks at `max-width: 900px`). Used by the LLM, Prompts and Provider settings pages.
  - Overview: `app/pages/project/overview-page.css:12-18`, `:58` (a 320 px side column, stacks at 1100 px).
  - Cast: `app/pages/cast/cast.css:105`. Pronunciations: `app/pages/pronunciations/pronunciations-page.css:90`, `pronunciation-editor.css:73`. Providers: `app/pages/settings/providers/provider-settings-page.css:63`. Setup check: `app/pages/settings/setup-check/setup-check-page.css:118`. Voice editor: `app/pages/voice-editor/voice-editor-page.css:26`.
- **What goes wrong**: on a 1280 px window with the rail expanded and the drawer open, `.shell__main` has about 1280 − 240 − 420 − 48 (padding) ≈ 570 px of content. The master–detail still puts the 320 px list beside an editor of about 235 px, and the Overview's details column stays at 320 px. Open the History sheet as well (the two are docked side by side in the flex row) and the page is about 190 px wide. The fixed 320 px track is then wider than the page, which scrolls sideways or squeezes the editor to nothing. Below 900 px the panels float over the page instead (`shell.css:353-395`), so there the viewport query happens to be right.
- **Fix shape**: `container: page / inline-size` on `.shell__main`, then turn the page breakpoints into `@container page (…)`. The shell's own `@media (max-width: 899.98px)` stays, because it does describe the viewport.
- **Firefox**: container size queries ship (guide: "Firefox 110 (Feb 2023)"). No fallback needed.
- **Watch**: `container-type: inline-size` adds layout containment, which makes `.shell__main` the containing block for `position: fixed` descendants. The one fixed element on a page, the pronunciation pill (`app/pages/book/pronunciation-pill.css:4`), is a popover in the top layer, so it should not be affected. The proof should confirm that.
- **Size**: M. Shared: shell plus the Master–detail primitive, and about 8 page stylesheets.

### K2. Dialogs are wider than a 320 px viewport

- **Guide**: `form-factor-adaptation`: "Ensure text and controls are never clipped, squished to zero dimensions, or overflowing at any viewport width down to 320 CSS pixels (WCAG 1.4.10)."
- **Where**: `styles/global.css:433-436`: `dialog.r2m-dialog { min-width: 320px; max-width: min(560px, calc(100vw - 32px)); padding: 24px }`, content-box. Content that sets its own floor: `app/pages/book/manual-reread-dialog.css:5` (`min-width: 420px`) and `app/pages/book/add-titles-dialog.css:5` (`min-width: 360px`).
- **What goes wrong**: `min-width` beats `max-width`, so the dialog is at least 320 + 48 = 368 px wide (468 px for the manual re-read dialog). Below that width (a phone, or 400 % zoom on a 1280 px screen) a centred modal can't fit. With `margin: auto` over-constrained, its inline-end edge runs off-screen, and that is where `.r2m-dialog__actions` puts Confirm/Save (`justify-content: flex-end`). A modal `<dialog>` is fixed, so the user can't scroll to it. Most other dialog content already uses `min(…px, NNvw)` and is fine.
- **Fix shape**: `min-width: min(320px, 100%)` (or `box-sizing: border-box` plus `min()`) on the dialog, and `min(…, 100%)` on the two content floors.
- **Firefox**: plain CSS, ships.
- **Size**: S. Shared: Dialog primitive (every `openDialog`, Confirm, Text prompt). Overlaps the top-layer area (#446/#447).

### K3. The collapsed AI activity stream is still focusable and read aloud

- **Guide**: `calculate-with-intrinsic-sizes`: "**MANDATORY**: Collapsing to zero size (`block-size: 0` or `height: 0`) hides content only visually: screen readers still read it, and its links and controls stay in the tab order. Set `visibility: hidden` on the closed state and `visibility: visible` on the open state, and include `visibility` in the transition so the content stays visible until it has finished closing."
- **Where**: `styles/global.css:2157-2164` (`.r2m-stream-disclosure__body { height: 0; overflow: hidden; transition: height 0.15s }`). It wraps an always-mounted `<r2m-stream-llm>` (`ui/stream-disclosure.ts:32-34`), whose turns are `<details>`/`<summary>` and which has a "Jump to latest" `<button>` (`ui/stream-llm.ts:76`, `:92`, `:98`, `:114`).
- **What goes wrong**: in Edit with AI, Generate voice prompt and Discover, with "Show AI activity" collapsed, Tab walks into invisible `<summary>` elements and the jump button. A focused one is off-screen, with no visible focus. Screen readers read the hidden stream in browse mode.
- **Fix shape**: add `visibility: hidden` (and `visible` on `--open`) and put `visibility` in the transition, as the guide says. `inert` on the closed body would also work. The height animation stays, because it runs between two lengths (0 and `32vh`) and needs no `interpolate-size`. Optionally add `aria-controls` on the toggle.
- **Firefox**: plain CSS, ships.
- **Size**: S. Shared: Stream disclosure. Overlaps accessibility (#448) and motion (#454).

### K4. Hover-revealed row controls are invisible on touch devices

- **Guide**: `natural-interactions`: "Controls or details revealed in place on hover, such as card actions, must also appear on keyboard focus and be visible by default on devices without hover", with `@media (hover: none) { opacity: 1; }`.
- **Where**: the Node menu triggers start at `opacity: 0` and show only on `:hover`, `:focus-within` or an open menu. That covers paragraph and item (`app/pages/book/book.css:329-348`), heading (`:175-183`), pause (`:200-207`) and structure tree row (`app/pages/book/structure-tree.css:88-96`). Also the Inline edit pencil (`styles/global.css:1693-1701`). The focus half is already done.
- **What goes wrong**: on a phone or tablet nothing hovers. The reader's and tree's Node menu triggers are invisible, though still tappable, so the commands they hold (split, insert, rename, merge, delete, attribute this node…) can't be found by sight. The app does consider coarse pointers elsewhere (the speaker menu skips autofocus for them, and the bottom sheet is full width on phones).
- **Fix shape**: one `@media (hover: none)` block per stylesheet setting the triggers to `opacity: 1`.
- **Firefox**: media query, ships.
- **Size**: S. Shared: partly (Inline edit). The Node menu triggers are feature CSS.

### K5. Truncated labels can't be read in full

- **Guide**: `single-line-overflow`, "Reveal full text on demand": "Reveal on `:hover` and `:focus-visible` of the interactive element, never on hover alone … Hover and focus reveal nothing on touch. Give the full text another route". Also "Use a tooltip … when the full text can be exceedingly long" and "DO NOT rely on `title` alone".
- **Where**: these are cut with `text-overflow: ellipsis` and have no `data-tooltip`:
  - Breadcrumbs, mostly book titles: `app/shell/shell.ts:178-181`, CSS `app/shell/shell.css:46-55`.
  - The shell's next-step label: `shell.ts:307`.
  - Cast list character names: `app/pages/cast/cast-page.ts:303`, CSS `cast.css:71-76`.
  - Speaker menu names: `ui/speaker-menu.ts:135`, CSS `styles/global.css:903-906`.
  - Export file names: `app/pages/export/export-page.ts:364`.
  - Voice descriptions: `app/pages/cast/voices/voice-card.ts:198`, CSS `cast.css:483-488`.
  - Inline edit text: `ui/inline-edit.ts:107`, CSS `global.css:1688-1690`. Opening the edit shows the full value, so this one is lower priority.
  - Only the structure tree uses the `data-tooltip-overflow` opt-in (`app/pages/book/structure-tree.ts:230`). The project card's series line already has a tip (`ui/project-card.ts:123`).
- **What goes wrong**: two characters whose long names share a prefix look the same in the Cast list and the speaker menu, and there is no way to see which is which except opening each one. A long book title in the crumbs or an export file name can't be read at all.
- **Fix shape**: the app already has the mechanism. Add `data-tooltip=${text}` plus `data-tooltip-overflow` on the truncating element. Where that element is a non-focusable span inside a focusable row (cast row, speaker menu entry), add `data-tooltip-delegate` on the row, as the tree does. The tooltip is shown on `pointerover`, which a tap also fires, and on `focusin` (`ui/tooltip.ts:56-63`).
- **Nit**: Edit with AI and the sample picker also cut strings in script (`app/pages/book/edit-with-ai/edit-with-ai-dialog.ts:69-72` `preview()`, `app/pages/settings/audio/sample-picker-dialog.ts:17-19` `snippet()`). The guide says "the string must never be cut in script", because find-in-page and copying then see the cut text. The Edit with AI row opens a detail that shows the full value, which the guide accepts as a route ("a row opens a detail view"), so I would not ticket these on their own.
- **Firefox**: JS tooltip, already used in both.
- **Size**: S. Shared: Speaker menu, Inline edit.

### K6. Focus indicators that are too faint or vanish in forced colours

- **Guide**: `natural-interactions`: "DO NOT remove focus outlines (`outline: none` or `outline: 0`) without providing an equally visible replacement with at least a 3:1 contrast ratio against the page (WCAG 1.4.11)." Also "Make each interaction state look different, including combinations: hover, press, pressing while hovering, and keyboard focus."
- **Where**:
  - Inline edit display button: `styles/global.css:1677-1680`. `:hover, :focus-visible { background: var(--r2m-surface-high); outline: none; }`. Focus is only a surface tint, the same as hover.
  - Speaker menu search input: `global.css:862-870` has `outline: none`. Its row (`.r2m-speaker-menu__search`, `:854-860`) has no `:focus-within` ring, unlike the Config list search (`:2354-2357`) and the ring pattern at `:285-288`.
  - Structure tree rows: `app/pages/book/structure-tree.css:18` (`outline: none`) with a `box-shadow: inset 0 0 0 2px` ring at `:25-27`. In forced-colours mode the browser forces `box-shadow` to `none`, so the tree's only focus indicator disappears under Windows High Contrast. The guide's example ring uses `outline`, which forced colours keep. That forced-colours behaviour is my reading of the CSS Color Adjustment spec, not a quote from the guide. The splitter already special-cases forced colours (`global.css:1751-1756`).
- **What goes wrong**: a keyboard user on the Overview's title or author, or on a voice name, can't tell focus from hover, and the tint is well under 3:1. In High Contrast the structure tree, the app's main keyboard surface, shows no focus at all.
- **Fix shape**: an `outline` ring on each, for example `outline: 2px solid var(--r2m-accent); outline-offset: -2px` as `.r2m-menu__item` does. For the tree, keep the box-shadow and add `outline: 2px solid transparent`, which forced colours paint as a visible ring.
- **Firefox**: ships.
- **Size**: S. Shared: Inline edit, Speaker menu. Overlaps accessibility (#448): triage should decide which area owns it.

### K7. Targets below 24 px, and no touch sizing

- **Guide**: `form-factor-adaptation`: "Make every control at least 24×24 CSS pixels (WCAG 2.5.8), and at least 44×44 on touch screens, where a fingertip is less precise than a mouse pointer (WCAG 2.5.5) … For checkboxes and radios, wrap the input in its `<label>` and size the label. To keep small icons visually compact, extend their hit area with a pseudo-element."
- **Where**: 13 × 13 px selection checkboxes on reader paragraphs (`app/pages/book/book.css:268-273`), on items (`:315-321`) and on tree rows (`app/pages/book/structure-tree.css:48-53`). The compact speaker chip is 20 px tall and is a button when it has a roster (`styles/global.css:819-822`). Nothing in the app sizes for `(pointer: coarse)`.
- **What goes wrong**: selecting paragraphs to attribute or generate is a core reader action, and a 13 px box next to other controls is hard to hit with a mouse and very hard on touch. Some of these may pass WCAG 2.5.8's spacing exception, which needs a 24 px circle that touches no other target. Triage should measure that rather than assume it.
- **Fix shape**: a `--r2m-target-size` token (24 px, or 44 px under `(pointer: coarse)`). Grow each checkbox's hit area with a sized wrapper label or an `::after` inset, keeping the 13 px look, and give the compact chip a pseudo-element hit area.
- **Firefox**: ships.
- **Size**: M (the reader rows are dense, so check it with the Measured list's row heights). Shared: Speaker chip, Checkbox.

### K8. Checkpoint pills reordered with `order`

- **Guide**: `css-layout` §2: "Don't use `order` or `flex-direction: *-reverse` to reorder interactive content. They change visual order only; the DOM order still drives sequential focus, so keyboard tab flow won't match what the user sees."
- **Where**: `app/pages/project/overview-page.css:72-75`. Below 1100 px, `.r2m-pipeline__checkpoint { order: 1; flex-basis: 100% }` moves the checkpoint `__pill` buttons to a second line, after the spacer and the `?` help toggle. In the DOM they come before the toggle (Pipeline primitive: `__title`, `__chip`, `__checkpoint`, `__hint`, `__spacer`, `__help-toggle`). That is the only `order` in the app.
- **What goes wrong**: on a narrow Overview, Tab goes title row → pills (line two) → `?` (line one), so focus jumps down and back up.
- **Fix shape**: change the DOM order in the `pipeline()` partial so it matches the narrow layout. The wide layout keeps everything on one line, so reading order still holds there. Or lay the head out with grid areas.
- **Firefox**: n/a.
- **Size**: S. Shared: Pipeline partial.

### K9. Scrolling tables aren't keyboard-scrollable regions, and most tables have no name

- **Guide**: `responsive-table`: "Add a `tabindex="0"` attribute to the container, to ensure that people using only a keyboard can consistently scroll the table. Give the wrapper a `role="region"` attribute … Provide it with an accessible name". `form-factor-adaptation`: "Make that container keyboard-scrollable with `tabindex="0"`, and give it `role="region"` and an accessible name."
- **Where**:
  - The Series cast board scroll wrapper has none of the three (`app/pages/projects/series-cast-board.ts:136`, `.series-cast__scroll { overflow-x: auto }` at `series-cast-board.css:11-13`). It has one column per book, so a long series overflows.
  - Setup check at 700 px and below sets `display: block; overflow-x: auto` on the `<table>` itself (`app/pages/settings/setup-check/setup-check-page.css:118-123`), with no wrapper.
  - No `<caption>` or accessible name on these tables: `series-cast-board.ts:137`, `app/pages/projects/series-voice-editor.ts:291`, `app/pages/pronunciations/pronunciations-page.ts:186`, `setup-check-page.ts:116` and `ui/throughput.ts:95`. The voice-rules preview has an `aria-label` (`app/pages/cast/voice-rules/voice-rules-section.ts:91`).
- **What goes wrong**: a keyboard user can't scroll the cast board to read-only columns past the edge. Tabbing only reaches cells that hold a control. A screen reader lists the tables as unnamed.
- **Fix shape**: a wrapper with `role="region" tabindex="0" aria-labelledby` pointing at a `<caption>` (or an `aria-label`). The setup-check overflow moves onto that wrapper.
- **Firefox**: ships.
- **Size**: S. Shared: Throughput (minor). Overlaps accessibility (#448).

### K10. The Activity bar's pill row scrolls with no visible cue

- **Guide**: `scrollability-affordance-hints`: "If the scrollbar is hidden, the hints are the only cue that the region scrolls, so you MUST use a fallback." It also says to use "a **fade** when: The scroller scrolls horizontally through a row of items (tabs, chips, carousels)."
- **Where**: `app/activity/activity.css:21-28` (`.activity-bar__pills { overflow-x: auto; scrollbar-width: none }`).
- **What goes wrong**: with more running jobs than fit, the extra job pills are cut off at the edge and nothing shows that the row scrolls. Mouse users have no scrollbar to drag. The pills' own focus rings are already inset (`styles/global.css:1181-1184`), so they aren't clipped.
- **Fix shape**: the guide's mask fade (registered `--fade-start`/`--fade-end` and `animation-timeline: scroll(self inline)`, about 25 lines of CSS, no JS). The smallest fix is to drop `scrollbar-width: none`, or set it to `thin`.
- **Firefox**: scroll-driven animations ship (guide: "Firefox 159 (Oct 2026)"). Before that version the fade doesn't show and the row still scrolls.
- **Size**: S. Shared: no.

### K11. Cast sort: no announcement, and names with numbers sort as text

- **Guide**: `dynamic-sort`: "Add one visually hidden `role="status"` element and set its text once per reorder … Name the result, not the mechanism: "Sorted by price, low to high, 24 items"". `sortable-table`: "Compare text with `Intl.Collator` (`numeric: true, sensitivity: 'base'`), not `<` or `localeCompare` in a loop."
- **Where**: the Cast page's sort `<select>` (`app/pages/cast/cast-page.ts:234-246`) and `sortRows` (`app/pages/cast/cast-rows.ts:60-77`, which calls `localeCompare(…, { sensitivity: 'base' })` without `numeric`). The rows already move as keyed nodes through `repeat` (`cast-page.ts:286`), so the reorder itself is right, and focus stays on the native select.
- **What goes wrong**: a screen reader user hears the new select value, but nothing about the result (the search filter applies too). "Guard 10" sorts before "Guard 2".
- **Fix shape**: one hidden `role=status` line ("Sorted by lines, 42 characters"), and a module-level `Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })`.
- **Firefox**: ships.
- **Size**: S. Shared: no. Overlaps accessibility (#448). The app has no `aria-live` region yet (map Notes), so whoever adds the first status pattern should settle it once.

### K12. Full-screen dialog and bottom sheet use `vh`

- **Guide**: `responsive-design`: "Use dynamic viewport units (`dvh`, `dvw`) instead of `vh`/`vw` to prevent layout breakage when mobile browser UI elements (like address bars) appear or disappear." `css-layout` §7: "Use `dvh`/`dvw` for mobile layout containers that must account for browser UI shifting".
- **Where**: `styles/global.css:487-495` (`dialog.r2m-dialog--fullscreen { width: 100vw; height: 100vh; max-height: 100vh }`, used by Edit with AI) and `:480` (`dialog.r2m-dialog--sheet { max-height: 80vh }`, the preflight sheet). The shell already does it right with a `vh` fallback (`app/shell/shell.css:5-6`).
- **What goes wrong**: on a phone with the address bar showing, the full-screen dialog is taller than the visible viewport, and its footer (Apply / Cancel) sits under the browser's toolbar. `100vw` causes no horizontal overflow here, because the root never scrolls (`app-root` is `overflow: hidden`), so only the height matters.
- **Fix shape**: add a `dvh` line after each `vh` line.
- **Firefox**: ships (guide: "Firefox 101 (May 2022)").
- **Size**: S. Shared: Dialog (Full-screen, Bottom sheet). Overlaps top layer (#446).

### K13. Main scroller: no stable gutter, and nested scrollers chain into it

- **Guide**: `css-layout` §6: "Use `scrollbar-gutter: stable` to reserve space for scrollbars and prevent layout shifts when content grows." Also "Use `overscroll-behavior: contain` (or `none`) on nested scroll regions (such as sidebars, modals, and lists) to stop scroll chains from bubbling into the page".
- **Where**: `.shell__main` (`app/shell/shell.css:306-312`) is the page scroller and has no `scrollbar-gutter`. Nested scrollers inside pages: the config editor body (`styles/global.css:2451-2456`), the LLM and Provider settings lists (`max-height: 70vh`, `app/pages/settings/llm/llm-settings-page.css:9`, `providers/provider-settings-page.css:9`) and the History sheet (`shell.css:325-333`). No `overscroll-behavior` appears anywhere in the app.
- **What goes wrong**: on Windows with classic scrollbars (the dev machine), page content jumps about 15 px sideways whenever a page grows past the window height, for example when a list finishes loading or a step's help opens. Wheeling to the end of a settings list then scrolls the whole settings page away from it.
- **Fix shape**: `scrollbar-gutter: stable` on `.shell__main` and `overscroll-behavior: contain` on the nested scrollers.
- **Firefox**: `scrollbar-gutter` ships (guide: "Firefox 97 (Feb 2022)"). The guide gives no compatibility line for `overscroll-behavior`. I believe it has shipped in both engines for years, but that is not from the guide, so confirm at triage.
- **Size**: S. Shared: shell. Lowest priority of the keeps.

## Drop

| # | Guide and rule | Why it's dropped |
|---|---|---|
| D1 | `css-layout` §5: "Feature-detect with `@supports (anchor-name: --x)` and provide an absolute-position fallback" (its compat line: anchor positioning "Unsupported in: Firefox") | The guides disagree with each other. `natural-interactions` gives the implicit anchor reference as "Firefox 147 (Jan 2026)". The menus, Node menu and tooltip already run in the Firefox E2E browser suite. This is already done, and belongs to top layer (#446) if anywhere. |
| D2 | `responsive-table`: sticky column and row headers | An enhancement, not a failure. The longest table (Pronunciations) has a search box and few columns, and it scrolls inside `.shell__main`, so a sticky header would also need `scroll-padding` (WCAG 2.4.11). Optional, if triage wants it. |
| D3 | `responsive-table`: stacked card layout under `@container` | A new feature (L). The one wide table (Series cast board) is meant to scroll. K9 covers the scroll region. |
| D4 | `state-aware-sticky-headers` (scroll-state `stuck`) | No surface: the app has no `position: sticky`. The feature is also Chromium-only ("Unsupported in: Firefox and Safari"). |
| D5 | `scroll-position-aware-elements` (scroll-state `scrollable`) | The one candidate, the LLM/audio stream's "Jump to latest", is JS autoscroll state that already works in both browsers. The CSS feature is Chromium-only. |
| D6 | `content-first-noise-reduction`: hide the top bar while scrolling down | Scroll-state is Chromium-only. The app bar is a single compact row and the page scrolls inside `.shell__main`, not the root. Popovers and `<details>` are already used for secondary UI. |
| D7 | `animate-to-intrinsic-sizes`, `calc-size()`/`interpolate-size` (expansion panel and disclosure height to `auto`) | Chromium-only ("Unsupported in: Firefox and Safari"). The fallback is an instant jump, which is how it behaves today. Motion area (#454). |
| D8 | `css` §2/§3/§5: avoid BEM, use `@layer`/`:where()`, nesting, tiered tokens, logical properties | Style-only. The BEM classes are the E2E hooks (conventions 1), and there is no RTL target. Out of scope under the map's rule. |
| D9 | `reduce-style-repetition` (`@function`), `dynamic-sibling-styling`, `individual-transform-properties`, `fluid-scaling`, `responsive-design` fluid type | No surface, or style-only. `@function` is Chromium-only. No hover transforms change stacking. Type is fixed-scale by design. |
| D10 | `design-token-reactivity`, `usage-aware-component-variations`, `selector-atrule-combinations` (style queries) | No surface. Theme branching is already one mechanism: `data-theme` pins `color-scheme`, and `light-dark()` tokens follow (`styles/global.css:28-29`, `:107-112`). |
| D11 | `child-state-based-styling`, `content-based-styling` (`:has()` fallbacks) | Already done. `:has()` is used (`app/pages/book/book.css:341-358`, `structure-tree.css:21`) and ships in Firefox 121. The map's browser gate needs no fallback. |
| D12 | `overflow-clipping-control` (`overflow-clip-margin`) | Firefox-only ("Supported by: Firefox 148"). No clipped shadows found. |
| D13 | `component-specific-light-dark-theme` | No surface. The theme editor's preview strip uses explicit inline colours, is `aria-hidden` and holds no native controls (`app/pages/settings/themes/theme-editor-dialog.ts:108-121`), so it needs no `color-scheme` override. The inverted tooltip is a simple surface. |
| D14 | `sortable-table`; `dynamic-sort` view-transition animation | No sortable table columns exist. Animating the Cast re-sort is a motion enhancement (#454). Its announcement half is K11. |
| D15 | `natural-interactions`: open popovers from the trigger (`transform-origin` and animation), swipeable scrollers, motion blur. `form-factor-adaptation`: safe-area insets, `text-wrap` | Motion (#454) or no surface: no carousels, no `viewport-fit=cover` (`index.html:5`), and `text-wrap` is style-only. |

## Guides with no surface in the app

`state-aware-sticky-headers`, `scroll-position-aware-elements`, `sortable-table`, `component-specific-light-dark-theme`, `overflow-clipping-control`, `reduce-style-repetition`, `dynamic-sibling-styling`, `design-token-reactivity`, `usage-aware-component-variations`, `selector-atrule-combinations`, `fluid-scaling`, `individual-transform-properties`, `atrule-support-conditionals` (the one `@supports`, for `base-select`, is a property test, which is the right tool) and `animate-to-intrinsic-sizes` / `calculate-with-intrinsic-sizes` (apart from K3's visibility rule).

## Cross-area overlaps for the map

- K2 and K12 (Dialog sizing) also belong to **top layer** (#446/#447).
- K3, K6, K9 and K11 also belong to **accessibility** (#448/#449). K11 is the app's first status announcement and should follow whatever pattern #448 settles.
- K3 (disclosure transition) also belongs to **motion** (#454/#455).
- K10's scrollbar choice touches **visual design / scrollbars** (#460).
