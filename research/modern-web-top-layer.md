# Top layer against modern-web-guidance (#446)

Research for #446 under map #444. The question: where does `src/Read2Me.Web` fall short of the modern-web-guidance guides for the top layer (dialogs, popovers, menus, tooltips, toasts), in ways that change behaviour or robustness?

- **Source**: the guide markdown bundled with the `modern-web-guidance` skill (`~/.claude/skills/modern-web-guidance/guides/`, skill version `2026_11_10-47b4c80e`), read locally. No `npx … retrieve` was run.
- **Code**: read at `13fc2cb2` (main, 2026-11-24). Paths are relative to `src/Read2Me.Web/src/`.
- **Filter**: the map's Notes. A finding is kept only if it changes behaviour or robustness and passes the browser gate: it ships in Chromium and Firefox, or has a fallback of about 20 lines or less with no dependency.
- **Compatibility**: taken from each guide's "Fallback strategies" section. The one place where that data conflicts with the repo's own evidence is called out in D7.

Result: **13 keep, 15 drop.** The three that matter most are K2 (Escape and the backdrop throw away unsaved work), K7 (blocked reasons can't be reached with a screen reader) and K10 (six `role="menu"` popovers with no menu keyboard).

## Kept findings

Ordered by impact within each primitive.

### K1. No modal dialog has an accessible name

- **Guide**: `dialog`: "**DO** name every dialog with `aria-labelledby` pointing at its heading, or `aria-label` when there is no visible title." The same rule is in `light-dismiss-a-dialog` ("**DO**: Use `aria-labelledby` or `aria-label` to provide an accessible name for the dialog").
- **Where**: `app/core/dialog.ts:45-50`. `openDialog` creates a bare `<dialog class="r2m-dialog">`. Every content element renders a visible `<h2 class="r2m-dialog__title">` (for example `app/ui/dialogs.ts:56`), but nothing points at it. The repo has no `aria-labelledby` or `aria-label` on any dialog. The only `role="dialog"` is the pronunciation card, which is named.
- **What goes wrong**: a screen reader announces "dialog" with no name when focus moves in. The user hears only the focused control ("Delete, button") and not what is being asked ("Delete project?"). This affects every confirm, prompt, preflight sheet and feature dialog.
- **Firefox**: ships. This is plain ARIA.
- **Size**: S. It touches a **shared primitive** (Dialog / `openDialog`). After the render `openDialog` already awaits, it can give the first `.r2m-dialog__title` an id and set `aria-labelledby`. An `ariaLabel` option would cover the full-screen dialog if it has no title.
- **Verdict**: **keep**.

### K2. Escape and the backdrop throw away unsaved work

- **Guide**: `dialog`: "Keep the unsaved-changes guard in a `cancel` handler, not in `closedby="none"` alone". Also: "**DO** use `closedby="none"` with an explicit confirmation step for dirty forms" and "**DO** use `command="request-close"` or `requestClose()` for Cancel buttons that must honor unsaved-changes logic".
- **Where**:
  - `app/core/dialog.ts:49` sets every dialog to `closedby="any"` unless the caller says otherwise. Its only `cancel` hook is `preventCancel` for `closedBy: 'none'` (`:27-34`).
  - **Edit with AI**: `app/pages/book/edit-with-ai/edit-with-ai-dialog.ts:576-579` (`closeReview`) asks `confirmDiscard` before it drops hand-edited proposals (`:589-597`). But the dialog is opened with the default `any` (`:613`), so Escape closes it without asking and the hand edits are lost. The guard exists only on the button.
  - **Theme editor**: `app/pages/settings/themes/theme-editor-dialog.ts:307` uses the default `any`. One stray click on the backdrop or one Escape discards a half-built theme (`draft`, `:56`) with no confirm. New project (`app/pages/projects/new-project-dialog.ts:52`) already moves to `closerequest` for this reason, so the app knows about the risk but handles it per dialog.
- **What goes wrong**: a user loses work they can't get back. Escape is pressed out of habit (for example to close a `<select>` or the speaker menu and pressing it once too often), and a backdrop click is easy to make by accident.
- **Firefox**: ships. `closedby` is in Firefox 141 and the `cancel` event has been available for a long time. Caveat from the guide: "do not rely on `preventDefault()` for repeated `Esc` presses without user activation". The guard must ask on the first `cancel`, and a second Esc in a row may still close the dialog in Chromium.
- **Size**: M. It touches a **shared primitive** (Dialog). Add an `openDialog` option or a content hook (`canClose(): Promise<boolean>`) that the dialog calls from its `cancel` listener, with `preventDefault` then a confirm. Default form-like dialogs to `closerequest`. Then use it in Edit with AI and the theme editor.
- **Verdict**: **keep**. This is the most user-visible finding.

### K3. Destructive confirms put focus on the destructive button

- **Guide**: `dialog`: "**DO** put `autofocus` on the safe action (such as Cancel or Keep) in a destructive confirmation."
- **Where**: `app/ui/dialogs.ts:66-75`. `ConfirmDialog` always autofocuses the confirm button, including when `destructive` is true (Delete, Discard, the permanent-change confirms).
- **What goes wrong**: Enter or Space, pressed straight after the dialog opens (a double press, or a keyboard user expecting focus on Cancel), runs the delete or discard. The danger is greatest for `confirmPermanent`, which by definition guards a change that can't be undone.
- **Firefox**: ships.
- **Size**: S. It touches a **shared primitive** (Confirm dialog): move `autofocus` to Cancel when `d.destructive`. Specs that assert focus on confirm will need updating.
- **Verdict**: **keep**.

### K4. Toast announcements rely on live regions inserted with their text

- **Guide**: `persistent-toast-notifications`: "**DO** use `ariaNotify()` to announce the content present in toasts". The fallback: "Use an ARIA live region, ensuring that the container is present in the page on load, and then add the messages to the live region container". Also: "**DO** make the container a region, with an accessible name".
- **Where**: `app/ui/toast.ts:58-60,85`. Each toast is a new `<div role="status">` (or `role="alert"`) created with its text already set and then appended. The stack `.r2m-toasts` (`:94-97`) has no role or name.
- **What goes wrong**: screen readers reliably announce changes inside a live region that already exists. A region inserted already filled is often not read, especially `role="status"` in NVDA and JAWS. Saved, cancelled and "Book updated elsewhere" messages can go unheard, and there is no named region where a screen-reader user could find them later.
- **Firefox**: `ariaNotify()` ships in Firefox 150 and Chrome 141. The live-region fallback is about 10 lines and needs no dependency.
- **Size**: S. It touches a **shared primitive** (Toast). Call `document.ariaNotify?.(text + (actions ? ' Actions available' : ''))`, else write to one persistent live region. Give the stack `role="region" aria-label="Notifications"`.
- **Verdict**: **keep**.

### K5. Action toasts time out under focus and hover, have no close button, and drop focus

- **Guide**: `persistent-toast-notifications`: "If the user is focused on a control inside the toast, pause dismissal until focus has left the toast". Also: "**DO** provide an explicit 'Close' button within the toast" and "**DO** manage focus when a user manually dismisses a currently focused toast".
- **Where**: `app/ui/toast.ts:61-64,77-80,89`. `setTimeout(dismiss, 12000)` runs unconditionally. `dismiss` calls `toast.remove()`, and the toast has no close control. Today's only action toast is Pronunciations' "Show in Book" (`app/pages/pronunciations/pronunciation-writer.ts:72-77`).
- **What goes wrong**: a keyboard or screen-reader user who tabs to "Show in Book" can have the toast pulled out from under them, and focus falls to `<body>` (WCAG 2.2.1 Timing Adjustable). Error toasts (6 s) can't be kept open long enough to read and can't be dismissed early.
- **Firefox**: ships.
- **Size**: S to M. It touches a **shared primitive** (Toast): pause the timer on `focusin` and `pointerenter`, add a close button, and move focus to the next toast or back to the prior element on dismiss.
- **Verdict**: **keep**.

### K6. Toasts raised over an open modal are inert

- **Guide**: `persistent-toast-notifications`: "**DON'T** surface toast notifications in the background while a modal dialog is open… Instead, suppress the notification until the modal dialog is dismissed." Also: "Content outside of the dialog will be inert and unavailable to assistive technologies."
- **Where**: `app/ui/toast.ts:86-88` re-shows the stack "so the stack moves above a dialog opened after it". That makes the toast visible over the modal, but `showModal()` leaves everything outside the dialog inert. Dialogs that call the API and toast a `problem()` on failure while they stay open (new project upload, Generate voice prompt, Discover, the preflight sheet) hit this.
- **What goes wrong**: an error raised while a dialog is open is painted on screen but is inert and hidden from the accessibility tree, so it is never announced. Any action button on it can't be clicked. **To confirm in triage**: whether Firefox exposes a popover shown above a modal differently. The guide does not say.
- **Firefox**: ships. Deferring is plain script: hold toasts while `document.querySelector('dialog:modal')` is set and flush them on the dialog's `close`. Alternatively, announce through `ariaNotify` (K4), which does not depend on inertness.
- **Size**: M. It touches a **shared primitive** (Toast, and the Dialog's close hook).
- **Verdict**: **keep**. Triage may merge it with K4.

### K7. Blocked reasons can't be reached with a screen reader

- **Guide**: `interest-triggered-tooltips`: "a hint exposed as a description must only carry content the user can afford to miss… anything the user needs belongs in a toggletip or inline". Also: "**Hint whose trigger does something else on activation**… `role="tooltip"` on the hint and `aria-describedby` on the trigger".
- **Where**: `app/ui/partials.ts:113-118`. `gated` wraps a disabled control in `<span class="r2m-blockable" data-tooltip=${reason} tabindex="0">`. The span has no role, no name and no description. The reason lives only in the shared tooltip's text (`app/ui/tooltip.ts:37`), and nothing references it.
- **What goes wrong**: `docs/agents/web/user-messages.md` makes the blocked reason the user's only explanation of why a control is off ("reachable by hover, tap and keyboard"). A screen-reader user tabs to the wrapper and hears nothing, or only "clickable". The reason is never read, so they can't learn why Generate audio or Attribute is unavailable. This affects every gated control in the app.
- **Firefox**: ships (ARIA).
- **Size**: S. It touches **shared primitives** (Gated, Tooltip). Give the wrapper a visually hidden child with the reason and `aria-describedby` to it, or make the wrapper `role="note"` / `aria-label` = the reason. The tooltip stays the visual cue.
- **Verdict**: **keep**. This is the most important accessibility finding.

### K8. The shared tooltip is never tied to its trigger

- **Guide**: `interest-triggered-tooltips`: "**Hint whose trigger does something else on activation** (a link, a button with its own action): `role="tooltip"` on the hint and `aria-describedby` on the trigger". It also covers the case where the text equals the name: "add nothing". It says to wire this "upfront", not lazily on focus.
- **Where**: `app/ui/tooltip.ts:17-40`. One `role="tooltip"` popover has its text swapped on each show. No trigger gets `aria-describedby`. Most icon buttons repeat their `aria-label` in `data-tooltip`, which is correct as it is. Triggers whose tip says more than their name lose that text for assistive technology: status chips with a `tooltip` (`statusChip`), `countBadge`, the job pill's tooltip, the series line on project cards.
- **What goes wrong**: supplementary text on chips and badges, such as counts and what a status means, is visual only.
- **Firefox**: ships. The guide's "point `aria-describedby` at the hint" works with one shared hint only while it is open. The robust form renders the description per trigger: the partials that set `data-tooltip` with text other than the name also add a visually hidden description.
- **Size**: M. It touches **shared primitives** (Tooltip, Status chip, Count badge, Job pill). Each partial needs its own small change.
- **Verdict**: **keep**, at lower priority than K7.

### K9. The tooltip can't be hovered (WCAG 1.4.13)

- **Guide**: `interest-triggered-tooltips`: "**Hoverable:** The pointer must be able to move over the tooltip content itself without the tooltip disappearing. This allows users with magnification tools to read the tooltip text safely."
- **Where**: `styles/global.css:590` (`.r2m-tooltip { pointer-events: none }`) and `app/ui/tooltip.ts:56-60`. Any `pointerover` whose target is not a `[data-tooltip]` element hides the tip, and the tip itself can't be a target.
- **What goes wrong**: a screen-magnifier user moving toward a long tip, such as a blocked reason or an overflowed title, loses it as soon as the pointer leaves the trigger.
- **Firefox**: ships.
- **Size**: S. It touches a **shared primitive** (Tooltip). Drop `pointer-events: none`, treat `pointerover` on the tip as keeping it open, and add a short hide delay as the pronunciation card already has (`HIDE_DELAY_MS`). Caveat from `toolbar`: keep the tip off the stacking axis so it can't cover the next control. `position-area: block-end` already does that for horizontal bars.
- **Verdict**: **keep**.

### K10. Six `role="menu"` popovers have no menu keyboard

- **Guide**: `menu`: "Use the menu pattern only for a composite widget with one tab stop and arrow-key navigation." `viewport-aware-dropdowns`: "Do not apply `role="menu"` or `role="menuitem"` unless you fully implement the corresponding keyboard navigation contract". From `menu`: "**Keyboard open enters the menu.** Opening with `Enter` or `Space` focuses the first enabled item."
- **Where**: `role="menu"` plus `role="menuitem"` popovers with no `keydown` handling:
  - `app/ui/project-card.ts:85-118`
  - `app/ui/config-list.ts:144-155`
  - `app/pages/projects/series-card.ts:110-120`
  - `app/pages/settings/llm/chain-card.ts:157-163`
  - `app/pages/book/book-page.ts:618-626` (Book actions)
  - the `menuItem()` partial (`app/ui/menu.ts:34-37`) stamps `role="menuitem"` on every entry
  - the shell theme menu (`app/shell/shell.ts:203-210`) has `menuitemradio` entries with no `role="menu"` container at all

  Only the node menu (`app/pages/book/node-menu.ts:412`, `rovingKeydown`) does it right.
- **What goes wrong**: screen readers announce "menu" and switch to focus mode, so the user presses the arrow keys and nothing moves. Focus also stays on the trigger after a keyboard open. Every entry is a separate Tab stop, which contradicts the role.
- **Firefox**: the guide's native path, `focusgroup="menu"`, is Chromium 150 only. Its own fallback is about 20 lines of `keydown`, and the app already has `rovingKeydown` (`app/ui/roving.ts:39`), so this passes the gate. The guide says to install the fallback only where `focusgroup` is missing. The simpler choice is to use the script in both browsers and not add `focusgroup` at all.
- **Size**: M. It touches a **shared primitive** (Menu). Add a `menuKeys` helper (Up, Down, Home, End, and focusing the first item on a keyboard open in `toggle`), then wire it into the six popovers. The S alternative is to drop the menu roles and treat them as plain button groups, which `viewport-aware-dropdowns` allows. Triage chooses.
- **Verdict**: **keep**.

### K11. The narrow-screen navigation rail is a modal drawer with no modality

- **Guide**: `navigation-drawer`: "**Also a sidebar**: use a regular element with `role="dialog"` and `popover="manual"`, and remove the `role` while it acts as a sidebar… Because a popover is not modal, the code must handle modality manually." It is dismissed by "tapping the dimmed backdrop… or pressing Escape". The trigger carries "`aria-expanded`… for assistive tech".
- **Where**: `app/shell/shell.ts:132-139,226-235`. Under 900 px the rail opens over a `.shell__scrim` that closes it on click. There is no Escape handling, the page behind is not `inert`, focus does not move into the rail, and the "Toggle navigation" button (`:160-167`) has no `aria-expanded`.
- **What goes wrong**: a keyboard user opens the rail and Tab walks into the dimmed page behind it. Escape does nothing. A screen reader is never told that a navigation panel opened.
- **Firefox**: ships (`inert`, popover, ARIA).
- **Size**: M. It is shell only, not a shared primitive. Add an Escape listener while open, `inert` on `.shell__main` and the app bar's other controls, focus the first nav link on open and the toggle on close, and `aria-expanded` on the toggle. The swipe and scroll-snap parts of the guide are new features and out of scope.
- **Verdict**: **keep**.

### K12. Pronunciation marks give assistive technology no cue that they open a card

- **Guide**: `hovercard` / `interest-triggered-tooltips`: "**Hint whose trigger also opens it on activation**… `role="tooltip"` on the hint and `aria-expanded` on the trigger, kept in sync with the popover's `toggle` event."
- **Where**: `app/pages/book/pronunciation-marks.ts:86-92` renders each mark as `<span class="r2m-pron-mark" tabindex="0">` with no role, name or state. The card (`app/pages/book/pronunciation-card.ts:255-257`) is a named `role="dialog"` popover that Enter moves focus into.
- **What goes wrong**: a screen-reader user tabbing through the reader lands on a word that announces only its text. Nothing says it is interactive or that Enter opens its pronunciation and a "This sentence" try-out.
- **Firefox**: ships.
- **Size**: S. It is feature code, not a shared primitive. Add `role="button"`, `aria-haspopup="dialog"`, and `aria-expanded` synced while the card is open for that mark.
- **Verdict**: **keep**.

### K13. The reader's `role="toolbar"` has no toolbar keyboard

- **Guide**: `toolbar`: "MANDATORY: Only one item is in the tab order at a time."
- **Where**: `app/pages/book/book-page.ts:309`. `<div class="book__toolbar" role="toolbar" aria-label="Reader">` holds a toggle, a segmented `radiogroup`, the pronunciations toggle, the selection bars and their menus. Each one is its own Tab stop, and there is no arrow handling.
- **What goes wrong**: screen readers announce a toolbar, and users expect the arrow keys to move between its items. Arrows do nothing, except inside the radiogroup, where they change the reader mode.
- **Firefox**: the fix the guide prefers (`focusgroup="toolbar"`) is Chromium only. A roving fallback would conflict with the nested radiogroup and the menus.
- **Size**: S if the role is removed, which is recommended: it is a bar of mixed widgets, not a toolbar. M for a real roving toolbar.
- **Verdict**: **keep** as the S fix.

## Dropped findings

| # | Guide | What | Why dropped |
|---|---|---|---|
| D1 | `declarative-dialog-popover-control`, `custom-button-actions` | Replace `popovertarget` and the script-opened `openDialog` with `commandfor` / `command` | Syntax only. Firefox 144 ships it, but `popovertarget` already gives the implicit anchor, toggle and light-dismiss grouping. `openDialog` builds dialogs from script, so no invoker exists to declare. No behaviour changes. |
| D2 | `interest-triggered-tooltips`, `hovercard` | Use `interestfor` for the tooltip and the pronunciation card | Chromium 149 only. The fallback is the `interestfor` polyfill, a dependency. The app already decided on JS triggers (`docs/agents/web/README.md`; `app/ui/tooltip.ts:4-5`). |
| D3 | `interest-triggered-tooltips` | `popover="hint"` instead of `manual` for the tooltip | Ships in Chrome 151 and Firefox 153, but adds nothing the installer lacks: it already hides on pointer-out, focus-out and Escape (`app/ui/tooltip.ts:56-66`), and `manual` already leaves open menus alone. |
| D4 | `prevent-page-scroll-under-modal-dialog`, `dialog` | `overscroll-behavior: contain` on `dialog:modal` and `::backdrop` | No surface. The document never scrolls: `app-root` is `100dvh; overflow: hidden` (`app/shell/shell.css:3-8`), and panes scroll inside it. Scroll chaining from a dialog reaches the viewport, which can't move. It needs Firefox 150 anyway. |
| D5 | `light-dismiss-a-dialog` | The `closedby` light-dismiss fallback script | Already done. `closedby` ships in Chromium 134 and Firefox 141, and `app/core/dialog.ts:31-34` already covers engines that close on Escape before honouring `closedby="none"`. |
| D6 | `platform-controls-dismiss-dialog` | Back gesture and Escape dismissal | Already done. Every dialog opens with `showModal()` (`app/core/dialog.ts:67`). |
| D7 | `viewport-aware-dropdowns`, `menu`, `interest-triggered-tooltips`, `persistent-toast-notifications` | Gate anchor positioning behind `@supports` or load the anchor polyfill, because the guides list "Anchor positioning… Unsupported in: Firefox" | **The guide's data conflicts with the evidence.** The same guide set lists `html.elements.button.popovertarget.implicit_anchor_reference` as "Baseline since 2026-01-13" (`user-experience/natural-interactions.md:145`; `menu.md:374`), which includes Firefox. The repo's Firefox E2E stage asserts that menus open on their trigger (`NodeMenuTests`, `BigBookTests`; `docs/agents/web/browser-tests.md:12`), and ADR 0014's prototypes passed in Firefox. The "unsupported" line most likely tracks a later sub-feature (the status reads "Chrome 157 (Nov 2026)"). No change. Worth flagging if the guides are re-read for the done check. |
| D8 | `persistent-top-layer-ui`, `move-dom-element-without-losing-state` | `moveBefore()` for reparenting open top-layer elements | No surface. No code reparents an open dialog or popover. The shared node menu, tooltip and pronunciation card re-anchor through `anchor-name` instead of moving (conventions 8). lit's keyed `repeat` could move a project card with an open menu, but lit-html owns those moves, and patching it is out of scope. |
| D9 | `position-aware-tooltips` | Anchored container queries for flipped arrows | No surface: no tooltip or menu draws an arrow. It is also Chromium 143 only. |
| D10 | `tabs` | Remove `aria-controls` (`app/ui/tabs.ts:46`). Use `hidden="until-found"` panels | `aria-controls`: the guide says no screen reader uses it, so removing it changes nothing a user meets. `until-found`: only the selected panel is rendered, so making the others findable is a new feature. The rest of the guide is already met: tablist name, `aria-selected`, roving with automatic activation, a labelled tabpanel, and an underline that doesn't rely on colour alone (`styles/global.css:1134-1137`). |
| D11 | `dialog` | Read and reset `dialog.returnValue` | No surface: there is no `<form method="dialog">`. Results come back through `r2m-close`. |
| D12 | `menu` | Submenus, the safe triangle, open and close animation | No shared menu has a submenu. The node menu's nested level has its own keyboard. Animation is style (motion is another area). |
| D13 | `light-dismiss-a-dialog` | Components inside a dialog that use Escape must capture it | Already done. Discovery's alias input stops Escape (`app/pages/cast/discovery-dialog.ts:302-305`), and inline edit (`app/ui/inline-edit.ts:117-119`) is not used inside a dialog. |
| D14 | `accordion` | — | Already done. Expansion panels are native `<details>` (`r2m-expansion`), with no arrow keys or roving, which the guide forbids adding. |
| D15 | `app-menus` | — | No surface: the app has no menu bar. |

## Guides with no surface in the app

- `ui-components/app-menus.md`: there is no menu bar.
- `ui-atoms/position-aware-tooltips.md`: there are no arrowed popovers.
- `ui-behaviors/persistent-top-layer-ui.md` and `ui-behaviors/move-dom-element-without-losing-state.md`: nothing reparents an open top-layer element.
- `ui-behaviors/prevent-page-scroll-under-modal-dialog.md`: the document does not scroll (D4).
- `ui-behaviors/custom-button-actions.md`: there are no custom `--commands`. The app uses no invoker commands at all (D1).

Every guide the ticket named was read. `accordion` and `tabs` had a surface but nothing to fix beyond D10 and D14.

## Notes for triage

- **Shared primitives touched by kept findings**: Dialog (K1, K2), Confirm dialog (K3), Toast (K4, K5, K6), Gated + Tooltip (K7, K8, K9), Menu (K10). Each primitive's PR updates its row in `docs/agents/web/primitives.md`, and K2 also updates conventions 6.
- **Cross-area overlap**: K4 and K6 (announcements) and K7 and K8 (descriptions) are also accessibility-area material. K2's confirm-on-dirty pattern overlaps the forms area.
- **Throwaway proofs**: K1, K3, K7, K8 and K12 can be shown by reading the accessibility tree from Playwright. K2, K5, K9, K10, K11 and K13 need keyboard or pointer driving in both engines. K6 needs a forced API failure while a dialog is open.
