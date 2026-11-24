# Accessibility audit against modern-web-guidance (#448)

Research ticket #448, under map #444. This checks `src/Read2Me.Web` against the guides for accessibility: announcements, focus, text scale and custom elements.

- **Source**: the guide markdown bundled with the `modern-web-guidance` skill (`~/.claude/skills/modern-web-guidance/guides/`, skill version `2026_11_10-47b4c80e`). It was read from disk only: no `npx … retrieve` call and nothing installed. Every quote below is verbatim from those files. Every Firefox verdict comes from the "Fallback / Browser support" data in the guide's own text. Where a guide gives no data, the finding says so.
- **Code**: read at `main` `13fc2cb2`, read-only. Paths are relative to `src/Read2Me.Web/src/`.
- **Filter**: the map's Notes. A finding is kept only if it changes behaviour or robustness. It must also pass the browser gate: it ships in Chromium and Firefox, or has a fallback of about 20 lines or less with no dependency.
- **Verdicts**: **CONFIRMED** means the code plainly does what the guide forbids, so the failure follows from the code alone. **PLAUSIBLE** means the code matches the anti-pattern, but the user-facing failure depends on browser or screen-reader behaviour that this desk read did not run. The throwaway E2E proof each implementation PR writes should settle those.

## Summary

| | Count |
|---|---|
| Keep | 17 |
| Drop | 9 |
| Guides with no surface | 3 |

The three that matter most:

1. **K1: every modal dialog is unnamed.** `openDialog` creates a `<dialog>` with no `aria-labelledby`. Every dialog the app opens announces as a bare "dialog", although each one has a visible `<h2 class="r2m-dialog__title">`. The fix is one shared primitive, size S.
2. **K2: toasts raised while a modal dialog is open sit in inert DOM.** That includes errors and the preflight sheet's "Live updates are disconnected". The toast stack is a top-layer popover outside the dialog, so `showModal()` makes it inert: it is neither announced nor operable. All toast announcements also rely on inserting a fresh `role=status`/`alert` node, which the guide calls unreliable. The fix is a central announcer plus toasts moved into the open dialog. Size M, shared primitive.
3. **K5: blocked reasons are inaudible.** `gated()` wraps a `disabled` control in a focusable generic `<span tabindex=0 data-tooltip>`. A keyboard or screen-reader user lands on an unnamed, roleless stop, and the reason exists only in a hover/focus tooltip that nothing references. The fix is `aria-disabled` on the control plus a programmatic description. Size M, shared primitive (used across every Edit gate).

---

## Keep

### K1: Modal dialogs have no accessible name. CONFIRMED

- **Guide**: `accessibility/accessibility` §12 example: `<dialog id="accessible-modal" aria-labelledby="title-id">`. §3: "Every interactive element and some landmarks need an accessible name". Also: "Prefer `aria-labelledby` over `aria-label` when a visible label exists".
- **Where**: `app/core/dialog.ts:45-67`. `openDialog` builds `document.createElement('dialog')`, appends the content and calls `showModal()`. It never sets `aria-labelledby`. Each content element renders `<h2 class="r2m-dialog__title">` with no `id`. There are 16 call sites, among them `ui/dialogs.ts:56,118` (confirm and prompt), `pages/book/edit-with-ai/edit-with-ai-dialog.ts:128` and `pages/projects/new-project-dialog.ts:62`. The preflight bottom sheet and full-screen variants go through the same function.
- **Failure**: on open, focus moves into the dialog and a screen reader says "dialog" with no title. A user who lands on a confirm dialog hears its buttons with no context for what they confirm.
- **Firefox**: plain `aria-labelledby` on `<dialog>`, which needs no compat data. Ships in both.
- **Size**: S. In `openDialog`, after `rendered()`, find `.r2m-dialog__title`, give it an id if it has none, and set `aria-labelledby` on the dialog. Shared primitive: Dialog, Confirm and Text prompt dialog, Bottom sheet and Full-screen dialog rows.

### K2: Toast announcements: inert under modals, fresh live-region nodes, many regions. PLAUSIBLE (inertness) / CONFIRMED (pattern)

- **Guide**: `accessibility/accessibility` §8:
  - "Don't add live-region updates to inert DOM: When dialogs open or sections become `inert`, queued or debounced messages will be unannounced — or announced from DOM the user can't reach."
  - "Centralize live regions for non-visible announcements: A single `polite` region and a single `assertive` region per page".
  - `accessibility/accessible-error-announcement`: "a live region that is shown, rather than changed, isn't announced reliably".
- **Where**: `app/ui/toast.ts:56-99`.
  - Each toast is a new `<div role="status|alert">` that is filled and then appended to a `popover="manual"` stack on `document.body` (line 60, line 85).
  - The comment at line 27 ("it sits above modal dialogs too") describes the visual stacking only.
  - Concrete cases:
    - `ui/preflight-sheet.ts:250` toasts "Live updates are disconnected" from inside the open modal sheet.
    - The themes page toasts problems after its editor dialog.
    - `pages/settings/setup-check/setup-check-page.ts:192-198` reports copy success or failure only through a toast.
- **Failure**:
  - While a modal `<dialog>` is open, everything outside its subtree is inert, the top-layer toast stack included. Screen readers then don't announce the toast, and its action buttons (`info(message, actions)`) can't be clicked or focused.
  - Even with no dialog open, a node inserted with its text already inside it is announced inconsistently across screen-reader and browser pairs.
- **Firefox**: ARIA live regions and `inert` ship in both. No fallback needed.
- **Size**: M.
  - Add one page-wide polite and one assertive announcer, as a `ui/announcer.ts` service.
  - `ToastService` writes each message's text there, and also renders the visible toast with `aria-hidden` or no role.
  - While a modal dialog is open, mount the stack (and the announcer) inside the top-most open `dialog`, so they are not inert.
  - Shared primitive: Toast. This is the "error announcement" overlap the map names: forms triage may own the field half (K15).
- **Proof**: open the preflight sheet with the hub stopped, press Start, then assert that the toast's role node is not in the accessibility tree (Playwright `ariaSnapshot`) and that its action button can't be clicked.

### K3: `statusChip` makes every chip a live region. CONFIRMED

- **Guide**: `accessibility/accessibility` §8: "too many regions, or noisy ones, quickly become spam for screen-reader users". Also: "Centralize live regions". And: "Don't use live regions for interstitial states like 'Loading…'".
- **Where**: `app/ui/partials.ts:33-40`, `role="status"` on every status chip. Chips render in lists: project cards, pipeline steps, preflight rows (`ui/preflight-sheet.ts:242`), Docker controls and job cards.
- **Failure**: a page with N chips has N polite live regions. A receipt-driven re-render that changes a few labels announces each change out of context ("Done", "Busy", "Done"…), and labels that change during a run (busy, then ok) chatter.
- **Firefox**: removing an attribute needs no compat data.
- **Size**: S. Drop `role="status"` from the chip. Where a state change matters (the end of a run), announce it once through K2's announcer. Shared primitive: Status chip.

### K4: Hub connection loss isn't announced. CONFIRMED

- **Guide**: `accessibility/accessibility` §8, urgency table: "Critical … `assertive` (or `role="alert"`) … Session timeout, API failure". Heuristic: "Use `assertive` only for … (e.g., data loss, session timeouts, or network drops)."
- **Where**: `app/shell/shell.ts:190-199`. `<span class="shell__conn" role="status" aria-label=${connectionLabel}>` holds the constant text `live`. Only `aria-label` changes with the state.
- **Failure**: live regions announce content changes, not name changes. When the hub drops or reconnects, the text stays "live", so nothing is announced. A screen-reader user keeps working while receipts no longer arrive.
- **Firefox**: plain ARIA, ships in both.
- **Size**: S. Announce state transitions (lost, back) through K2's announcer, assertive for lost and polite for restored. Keep the dot's name as `aria-label` on a non-live element (`role="img"`). Not a shared primitive (shell).

### K5: `gated()`: blocked reasons are unreachable for assistive technology. CONFIRMED

- **Guide**: `accessibility/accessibility` §2: "`disabled` removes the element from the focus order entirely … `aria-disabled="true"` keeps the element focusable so users can land on it and learn it's disabled, but … you must also prevent activation … in JavaScript." §5: "Don't apply `tabindex="0"` to static, non-interactive, and hidden elements". §3: "If the description is not visible on the page … apply the `aria-description` attribute".
- **Where**:
  - `app/ui/partials.ts:106-119`: while a gate has a reason, the control is rendered `disabled` inside `<span class="r2m-blockable" data-tooltip=… tabindex="0">`.
  - `app/ui/tooltip.ts:17-67`: the shared `role="tooltip"` popover is filled on hover or focus after 400 ms. No element references it with `aria-describedby`.
  - Used by every Edit gate: pipeline actions, inline edit, file drop, settings form, voice card and more.
- **Failure**: Tab lands on a generic span with no name or role. A screen reader says nothing useful, or "clickable". The disabled button inside is skipped, and the blocked reason is never exposed programmatically. That reason is the whole point of the gate in `docs/agents/web/user-messages.md`.
- **Firefox**: `aria-disabled` and `aria-describedby` ship in both. The guide gives no compat data for `aria-description`, so prefer `aria-describedby` pointing at a visually hidden reason node.
- **Size**: M.
  - `gated` hands the control `aria-disabled="true"` instead of `disabled`, plus a describedby id. It renders the reason in a visually hidden node, and blocks click and Enter/Space activation.
  - The wrapper span and its `tabindex` go away, and the tooltip keeps working off the control itself.
  - This changes the primitive's contract ("the `disabled` it hands in is the control's only source of `?disabled`"), so update the Gated row in `primitives.md` and the blocked-reasons section of `user-messages.md`. Shared primitive.
- **Proof**: Tab to a blocked pipeline action, then assert the focused element's role is `button`, its accessible description equals the reason, and a click doesn't run it.

### K6: Inline edit drops focus to `<body>` on Enter, Escape and blur-save. CONFIRMED

- **Guide**: `html/custom-elements`: "Replacing an element that has focus moves focus to `<body>`, and keyboard and screen reader users lose their place (WCAG 2.4.3 Focus Order)." `html/html` §6: "DO maintain focus—moving it to a logical adjacent element or returning it to the trigger control—when the currently focused element is hidden or removed."
- **Where**: `app/ui/inline-edit.ts:83-110,130-148`. `commit()` and `abort()` set `#editing` false, so the template swaps the focused `<input>` for the display `<button>`. Nothing refocuses the button afterwards, while `updated()` only handles the opening direction.
- **Failure**: a keyboard user renames a voice or project with Enter, and focus falls to `<body>`. The next Tab starts from the top of the page, and a screen reader loses its place.
- **Firefox**: needs no compat data.
- **Size**: S. After a commit or abort that came from a key (Enter or Escape), focus `.r2m-inline-edit__display` in `updated()`. Leave blur alone, since focus has already moved on. Shared primitive: Inline edit.

### K7: Inline edit's name doesn't contain its visible text. CONFIRMED

- **Guide**: `accessibility/accessibility` §3: "Any visible text used to label a control must be included exactly as written in the control's accessible name … (2.5.3 Label in Name (Level A))".
- **Where**: `app/ui/inline-edit.ts:101-109`. The button shows `value || placeholder` (for example "Mary Bennet") but is named `aria-label="Edit Voice name"`.
- **Failure**: a voice-control user who says "click Mary Bennet" gets no match. A screen reader hears "Edit Voice name" and never the current value.
- **Firefox**: needs no compat data.
- **Size**: S. Name it `Edit ${placeholder}: ${value}` (visible text included), or keep the visible text as the name and move "Edit …" to `aria-describedby`. Shared primitive: Inline edit.

### K8: `aria-label` on roleless icon spans: state hidden from assistive technology. CONFIRMED

- **Guide**: `accessibility/accessibility` §3: "Don't put `aria-label`/`aria-labelledby` on elements that shouldn't be named: Examples include plain `<div>`, `<span>` … These attributes are only intended for … widget …, landmark, image, or iframe roles." §6 for SVGs: "apply `role="img"` … for non-decorative".
- **Where** (all font-ligature `<span class="material-symbols-rounded">` with `aria-label` and no role):
  - `app/ui/speaker-chip.ts:152-157` ("Linked narrator")
  - `app/pages/book/structure-tree.ts:405` ("Done")
  - `app/pages/book/item-row.ts:197-203` ("Review dismissed")
  - The default-voice star `pages/cast/voices/voice-card.ts:157-162` has only a `data-tooltip` and an `aria-hidden` icon, so "Default voice" isn't exposed at all.
- **Failure**: browsers may drop a name on a generic element. Where they keep it, screen readers read the ligature text ("link", "check_circle") instead, since the span's text content is the icon name. Either way the state (linked narrator, chapter done, review dismissed, default voice) is lost or garbled.
- **Firefox**: `role="img"` + `aria-label` ships in both.
- **Size**: S. Add `role="img"` to these spans (the label then replaces the ligature text), or keep the icon `aria-hidden` and add visually hidden text. Consider an `icon(name, cls, label?)` overload in `partials.ts` so the pattern lives in one place. That would touch a shared primitive (Icon).

### K9: Loading skeletons: unnamed busy `div`s. CONFIRMED

- **Guide**: `ui-components/progress`: "Avoid `aria-busy` + `aria-describedby` on an unlabeled generic `<div>`. `aria-busy` has inconsistent screen reader support … If a region's content is not ready while loading, hide it with `hidden` or `aria-hidden="true"`." `accessibility/accessibility` §8: use "the HTML `<progress>` element or the ARIA `progressbar` role for this purpose instead."
- **Where**: `<div aria-busy="true" aria-label="Loading …">` at:
  - `pages/book/book-page.ts:417-421`
  - `pages/cast/cast-page.ts:266-270`
  - `pages/export/export-page.ts:337-341`
  - `pages/projects/projects-page.ts:232`
  - `pages/project/overview-page.ts:75-79`
  - `pages/book/structure-tree.ts:235` (a span)
  - `pages/projects/series-cast-board.ts:122` and `pages/projects/series-voice-editor.ts:247` (a `<p>` with text)
- **Failure**: the label sits on a generic element, so it is ignored or prohibited. `aria-busy` is announced inconsistently. A screen-reader user arriving at the chapter or the cast list gets silence or a bare skeleton, with no "Loading chapter".
- **Firefox**: `role="progressbar"` and `<progress>` ship in both.
- **Size**: S to M across 8 sites. Make each skeleton `aria-hidden="true"` and put a labelled indeterminate progress next to it (the existing `spinner()` or a `<progress aria-label>`). A small `skeleton(label)` partial would keep this to one place. If added, it is a new shared primitive with its own row.

### K10: Spinner and progress bar: motion, forced colours, invalid pseudo-element rule. CONFIRMED (rule) / PLAUSIBLE (rendering)

- **Guides**:
  - `ui-components/progress`: "MANDATORY: Do NOT group `-webkit-` and `-moz-` pseudo-elements in the same rule. The rule will be deemed invalid and will be ignored."
  - Same guide: "MANDATORY: Use `appearance: none` to enable custom styling." Also: "Never disable the indeterminate animation … For reduced motion, slow it down instead."
  - `ui-components/spinner`: "Always respect the `prefers-reduced-motion` media query." Also: "Forced colors mode removes background gradients … or the spinner disappears."
- **Where**: `styles/global.css:1044-1104`.
  - `progress:indeterminate { &::-webkit-progress-bar, &::-moz-progress-bar { background: transparent } }` is the grouped rule, so it is dropped in both engines.
  - `progress` is custom-styled (`background`, `border-radius`) without `appearance: none`.
  - The indeterminate sweep is a `background` gradient on `progress` itself, which is underneath `::-webkit-progress-bar`, still opaque `--r2m-surface-highest`.
  - `.r2m-spinner` is a border ring with `border-top-color: currentColor` and an `0.8s` spin.
  - No `prefers-reduced-motion` or `forced-colors` rule exists for either. `prefers-reduced-motion` appears only in `projects-page.css`.
- **Failure**:
  - In Chromium the indeterminate `<progress>` likely shows a flat track with no sweep, because the transparent override never applied.
  - Under forced colours, the determinate fill (a `background`) is repainted, and the spinner's four borders become one colour, so the rotation is invisible.
  - With reduced motion requested, both still animate at full speed.
- **Firefox**: the guide's data says CSS pseudo-elements, `@media (prefers-reduced-motion)` and `forced-colors` need no fallback. `revert-rule` (Firefox 150) is optional. `background-clip: border-area` is Chromium-only, so if the spinner moves to the guide's `<progress>` ring, it needs the guide's `@supports not` mask fallback (about 6 lines).
- **Size**: M. Split the pseudo rules, add `appearance: none`, animate the fill rather than the track, add a reduced-motion slow-down and a forced-colors `CanvasText` / `Highlight` rule. Shared primitives: Spinner, Progress bar.
- **Overlap**: the motion area may claim the reduced-motion half.

### K11: Expansion panel chevron is read aloud as "expand_more". CONFIRMED

- **Guide**: `accessibility/accessible-generated-content`: "Use `content: "text" / "";` to prevent purely decorative text from being announced to screen readers."
- **Where**: `styles/global.css:2185-2192`, `.r2m-expansion > summary::after { content: 'expand_more'; font-family: 'Material Symbols Rounded' }`.
- **Failure**: CSS generated text joins the summary's accessible name, so "Advanced settings" is announced as "Advanced settings expand_more". This hits every Expansion panel (voice card, LLM editor, prompt editor).
- **Firefox**: the guide says alt text for generated content is "Supported by: … Firefox 128 (Jul 2024)". Ships in both.
- **Size**: S, one line: `content: 'expand_more' / '';`. Shared primitive: Expansion panel.

### K12: Interactive controls inside a voice card's `<summary>`. PLAUSIBLE

- **Guide**: `html/html` §5: "DON'T nest other interactive elements (links, buttons) directly inside `<summary>` text as it may break expected focus behavior and screen reader announcements."
- **Where**: `pages/cast/voices/voice-card.ts:153-200`. The `<summary class="voice-card__header">` holds the "Set as default voice" button (through `gated`), an `<r2m-inline-edit>` (a button that becomes an input), and click/keydown stoppers (line 496 comment). `primitives.md`'s Expansion panel row documents the workaround ("controls inside a summary stop their clicks and keys").
- **Failure**: `<summary>` is exposed as a button, and a button's children are presentational. The nested button and input can be flattened into the summary's name ("star Set as default voice Mary Bennet edit"), and screen readers may not reach them as separate controls. The stoppers are needed only because of the nesting.
- **Firefox**: needs no compat data.
- **Size**: M. Keep only the title in `<summary>`, and move the star and rename to a header row outside it (or use the guide's `<h3><button aria-expanded>` disclosure). Update the Expansion panel row in `primitives.md`.

### K13: Breadcrumbs: no list, current page not a link, colour-only link cue, current page can truncate. CONFIRMED

- **Guide**: `ui-components/breadcrumbs`:
  - "Wrap the trail in `<nav aria-label="Breadcrumbs">` containing an ordered list (`<ol>`) of `<li>` items, each holding an `<a>`."
  - "Mark the current page, the last item, as a link with `aria-current="page"`."
  - "Underline links at rest, or at least on hover and focus, so they never rely on color alone."
  - "Long labels: … never truncate the current page."
- **Where**: `app/shell/shell.ts:173-183` (bare `<a>`/`<span>` siblings in `<nav>`, current as `<span aria-current>`), `app/shell/shell.css:37-60` (`text-decoration: none`, muted vs normal colour only, `overflow: hidden; text-overflow: ellipsis` on every crumb including the current one).
- **Failure**: assistive technology gets no "list, 3 items" for the trail depth. Links are told apart from the current page by colour alone (WCAG 1.4.1). A long book title as the current crumb is cut off with no way to read it.
- **Firefox**: the guide says `content: "/" / ""` works in Firefox 128+. Everything else needs no compat data.
- **Size**: S. Use `<ol>`/`<li>`, a CSS separator with empty alt (replacing the `aria-hidden` `›` span), underline on hover and focus, `flex-shrink: 0` on the current crumb, and the last crumb as a link. Not a shared primitive (shell).

### K14: No skip link past the app bar and section rail. CONFIRMED

- **Guide**: `accessibility/accessibility` §1: "Provide skip links prior to repeated content like site headers with navigation … so that keyboard users can easily bypass them. (2.4.1 Bypass Blocks (Level A))". It also covers making the target focusable with `tabindex="-1"` on the first heading.
- **Where**: `app/shell/shell.ts:158-249`. The app bar has 7 or more stops (menu, brand, crumbs, Next step, History, Setup shield, theme, settings), then the rail's nav items, before `<main>`. There is no skip link.
- **Failure**: every page load and route change costs a keyboard user over 10 Tabs to reach the page.
- **Firefox**: the guide's `.visually-hidden:where(:not(:focus-within, :active))` utility needs no compat data.
- **Size**: S. Add a first-in-DOM `<a href="#main" class="r2m-skip-link">Skip to main content</a>` and give `<main id="main" tabindex="-1">`. Because the app uses `<base href>` and Navigation API routing, the handler should focus `main` rather than navigate to the fragment. Adds a `.visually-hidden` utility to `global.css`, which K2 and K5 also need.

### K15: Field error: `role="alert"` inserted per error, `aria-invalid` unset before interaction. PLAUSIBLE

- **Guide**: `accessibility/accessible-error-announcement`:
  - "Don't make each error message a live region: a live region that is shown, rather than changed, isn't announced reliably."
  - "Announce an error that appears when the user leaves or changes a field through one polite live region for the page, a moment later so it doesn't talk over the next field."
  - "Set `aria-invalid="false"` until `:user-invalid` matches, rather than leaving the attribute unset."
- **Where**: `app/ui/field.ts:44-48` (the error `<p role="alert">` is inserted and also referenced by `aria-describedby`, `invalid: 'true' | nothing`), and `app/ui/inline-edit.ts:90` (sets `aria-invalid`, but nothing describes the error).
- **Failure**: an error shown while typing interrupts assertively mid-keystroke, and may be read twice (as the alert, then as the description). The inline edit's "invalid" state has no message.
- **Firefox**: `:user-invalid` is "Supported by: … Firefox 88". The guide's sync script is plain DOM. Ships in both.
- **Size**: S (2 `field()` callers). Route the announcement through K2's polite announcer on blur or change, and drop `role="alert"`. Shared primitive: Field.
- **Overlap**: this is the map's named accessibility/forms overlap, and the forms triage may own it.

### K16: Narrow-screen modal rail has no inert outside and no focus move. PLAUSIBLE

- **Guide**: `accessibility/accessibility` §12, custom overlays:
  - "When `<dialog>` cannot be used …, apply `inert` to outside content".
  - "Move focus to the overlay … Don't leave focus in outside content."
  - `html/html` §6 shows `<main id="app-shell" inert>` while a drawer is open.
- **Where**: `app/shell/shell.ts:226-249`. Under 900 px the rail opens over a `.shell__scrim` that closes on click. `<main>` and the app bar are never made `inert`, focus isn't moved into the rail, and no Escape handler was found in `shell.ts`.
- **Failure**: a keyboard user opening the rail on a narrow window keeps Tabbing through the covered page under the scrim. Screen readers read the obscured content. There is no keyboard way to close the rail besides re-pressing the toggle.
- **Firefox**: `inert` needs no compat data. A `<dialog>` with `closedby` is already used elsewhere (Firefox 141 per `core/dialog.ts:48`).
- **Size**: M. Either render the open narrow rail as a modal `<dialog closedby="any">`, which gives inertness, focus and Escape for free, or set `inert` on the app bar and main, focus the first nav item, and close on Escape. Not a shared primitive (shell).

### K17: Fixed-height chrome, px icon sizes and px breakpoints don't follow text scaling. PLAUSIBLE

- **Guide**: `accessibility/respect-os-text-scale`:
  - "Text-containing components and containers MUST be able to grow to accommodate scaled text. Avoid fixed `height` …"
  - "Responsive breakpoints MUST NOT assume that text will remain at its default size … prefer … `em`-based media queries".
  - "Test that text can be resized to at least 200% of its default size without loss of content".
- **Where**:
  - `styles/global.css:82-85` (`--r2m-appbar-height: 48px`, `--r2m-activity-bar-height: 40px`) used as `height` and `flex-basis` in `shell/shell.css:14-15,340`, with `white-space: nowrap; overflow: hidden` crumbs (`shell.css:37-45`).
  - About 25 `font-size: NNpx` icon sizes in `global.css`, for example `.material-symbols-rounded { font-size: 20px }` at line 128.
  - The `.r2m-checkbox input { width: 18px; height: 18px }` at line 297.
  - Nine `@media (max-width: NNNpx)` breakpoints (`shell.css:353`, `cast.css:105`, `overview-page.css:58` and others).
  - What already complies: the root font size is not overridden (`html` sets only `height`), body text uses `rem` tokens, and line height is unitless.
- **Failure**: with the browser's default font size raised (Chrome Settings → Font size, or Firefox's minimum font size or text-only zoom), the `rem` text grows but the 48 px app bar doesn't. Crumbs and buttons clip, icons and checkboxes stay small next to large text, and layouts switch on viewport px rather than on room for text.
- **Firefox**: these are plain CSS changes. `<meta name="text-scale" content="scale">` is "Supported by: Chrome 146 … Unsupported in: Firefox and Safari". The guide says it "degrades gracefully", so the meta passes the gate as a no-op in Firefox. But it is worth adding only after the fixed heights go, and its effect on desktop Windows isn't covered by the guide.
- **Size**: M. Use `min-block-size` for the bars, `em` icon sizes (`1.25em`), the checkbox at `1.125em`, and `em` breakpoints. Then optionally add the meta to `index.html`. Shared primitives: Icon, Checkbox, and shell layout.
- **Proof**: set the browser's default font size to 32 px in both engines, then assert that no app-bar control is clipped (`scrollHeight <= clientHeight`).

---

## Drop

| # | Guide and rule | Where it would apply | Reason |
|---|---|---|---|
| D1 | `html/custom-elements`: "Hide undefined elements … `:not(:defined)` … fail open" | `index.html` `<app-root>` | No surface. `<app-root>` is empty until script runs, and every other element is created by lit after `define()`, so no pre-upgrade content can flash. |
| D2 | `html/accessible-web-components`: "Set a custom element's role and ARIA state … through `ElementInternals`, rather than writing ARIA attributes onto the host" | `ui/splitter.ts:77-86`, `pages/book/pronunciation-panel.ts:72-73`, `voice-card.ts:144` (`aria-busy` on host) | Newer syntax only. The guide's own fallback is the host attribute the code already uses, and no consumer overrides them. There is no behaviour change. |
| D3 | `html/accessible-web-components`: "an `id` referenced by `aria-labelledby` … resolves only within the referencing element's own tree" | All elements | No surface. The app is light DOM with `@scope` (conventions 1). There are no shadow roots, so every id reference is in one tree. |
| D4 | `html/shadow-dom` (whole guide: `delegatesFocus`, templates, slots, `adoptedStyleSheets`) | All elements | No surface: no `attachShadow` anywhere in `src/`. |
| D5 | `html/custom-elements`: "If the component's styles set `display` on `:host`, also add `:host([hidden]) { display: none; }`" | `pages/book/pronunciation-panel.ts:76` sets `this.hidden` | Already done. Its scope sets `display: block` and also `:scope[hidden] { display: none }` (`pronunciation-panel.css:3-14`). |
| D6 | `html/custom-elements`: "Do not shadow global attributes (… `title`, `hidden` …) with your own properties" | `R2mElement` subclasses | Already done. Conventions 5 forbids naming members `remove`, `focus`, `hidden`, `title` or `click`. |
| D7 | `ui-behaviors/search-hidden-content`: "use either the HTML `<details>` element or the `hidden="until-found"` attribute" | Expansion panels, `tabs()` panels, help panel | Expansion panels and stream sections are already `<details>`, so they are searchable. Unselected tab panels are not rendered at all, so `until-found` would mean mounting every panel. That is new behaviour (and hub cost for stream tabs), not a fix. |
| D8 | `ui-components/copy-button`: "Declare the target with Invoker Commands (`command` + `commandfor`) … The confirmation bubble IS the live region" | `pages/settings/setup-check/fix-hint.ts:16-25` | Mostly done. The one copy button is a `type="button"` named `Copy <command>`, and failures are reported. Invoker Commands would be newer syntax only. The announcement gap (toast-only feedback) is covered by K2. |
| D9 | `ui-components/checkbox`: "provides an interactive hit target of at least `44px x 44px`" and "Native customization with `accent-color`" | `styles/global.css:293-301` | Already native with `accent-color`. The wrapping label meets the guide's AA rule ("at least 24x24", `accessibility` §5). 44 px is a stricter best practice, so it was dropped. The fixed 18 px size is folded into K17. |

## Guides with no surface in the app

- `html/shadow-dom`: no shadow roots (D4).
- `ui-components/progress-ring`: no determinate circular progress anywhere. The one ring is the indeterminate `spinner()`, covered by `ui-components/spinner` in K10.
- `html/accessible-web-components`, cross-root ARIA and `delegatesFocus` sections: light DOM only (D3). The `ElementInternals` section was read against the splitter (D2).

## Guides read

`accessibility/accessibility`, `accessibility/accessible-error-announcement`, `accessibility/accessible-generated-content`, `accessibility/respect-os-text-scale`, `html/accessible-web-components`, `html/custom-elements`, `html/shadow-dom`, `html/html`, `ui-components/progress`, `ui-components/progress-ring`, `ui-components/spinner`, `ui-components/breadcrumbs`, `ui-components/checkbox`, `ui-components/copy-button` and `ui-behaviors/search-hidden-content`.

`accessibility/accessibility` points at `color`, `typography` and `motion` through `npx … retrieve`. Those guides were not fetched (map Notes) and belong to other areas.

## Overlaps for the map's "Cross-area overlaps"

- K2 and K15 (error announcement) overlap with forms.
- K10 (reduced motion on spinner and progress) overlaps with motion.
- K16 (narrow rail as a modal) overlaps with top layer and dialogs.
- K2, K5 and K14 all want the same `.visually-hidden` utility and announcer. Ticket them in order: announcer and utility first (K2), then K3, K4, K5, K14 and K15, which build on it.
