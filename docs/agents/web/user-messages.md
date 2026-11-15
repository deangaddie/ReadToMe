# Messages to a new user

The partials live in `ui/partials.ts`, `ui/menu.ts` and `ui/dialogs.ts`; the wording and the rules for when each shows live in the feature folder, as pure functions with a table spec.

## Advice

Advice comes before the click, never inside a confirm: an out-of-order action shows its one sentence through `adviceLine` (under the action, above a dialog's own button) or a menu entry's `hint`.

## Blocked reasons

Every control that may be off answers one **Edit gate** (context/web.md), and its **blocked reason** and its `disabled` both come from that one answer, so they always agree.

- **One shape.** `Block | null` (`api/step-view.ts`) is the only gate shape: `null` is open, `{ reason: null }` is blocked with no reason shown, `{ reason }` is blocked with that reason. No prop, param or signal carries a `readOnly: string | null`; an element takes a `block` property.
- **One fold.** The pure `blockedReason(state, local?)` ranks the reasons: viewing a History step > stale > a section reason (a character's series Voices) > the control's local reason > a reasonless busy (saving, or a local `true`). At most one reason shows. Each store's gate is a `block` signal built through it: `ProjectStore.block`, `CastStore.block`, `CastStore.voicesBlock` (with the series section reason) and `BookEditor.block` (with stale and saving; `BookEditor` declines a write on the same answer). `StepView.readOnly(folder)` answers the viewing `Block` they start from.
- **Local conditions fold in through `withLocal(block, local)`**, never beside it: a busy, dirty or invalid flag goes in as `true`, and a feature's own reason, a pure `xBlockedReason(gate): string | null` in the feature folder's logic file (`editor-logic.ts`'s `applyBlockedReason`, never in `ui/`), goes in as its string. A local reason fills only a block that has none, so viewing, stale and a section reason win.
- **One gated control.** `gated(block, (disabled) => control, { tabbable? })` (`ui/partials.ts`) renders it: the `disabled` it hands in is the control's only source of `?disabled`, with nothing ORed into it. While the gate has a reason the control sits in the focusable `.r2m-blockable` tooltip wrapper; open, or blocked without a reason, it renders bare. Every control that can show a blocked reason uses it, in or out of a project (the Setup check's Re-run). A plain `?disabled` is fine only where no reason is ever shown. One exception: `r2m-settings-form` keeps its field `<div>` as the tooltip host (a `gated` span would change the grid), and takes its tooltip, `tabindex` and `disabled` from one folded gate all the same.
- The Node menu carries the gate it was opened with: a `NodeMenuRequest` holds the `Block` its trigger rendered with, and `openFor` and `runMenuEntry` refuse on that block rather than asking again, so the trigger and the menu never disagree. An item row's play button has its own gate, `RowContext.playBlock` (`ITEM_AUDIO_NOT_KEPT` while a step is viewed), since playback stays on while the view is stale.

A setting a step needs is named in prose in the blocked reason, plus a callout with an `href` action where a link matters.

A momentary busy carries no reason: the Book toolbar shows one busy `statusChip` "Saving…" (`data-testid=book-saving`), before the actions menu, while `BookEditor.saving` holds (the stale banner covers staleness).

## Callouts and errors

A **callout** comes from one pure `xCallout(input): Callout | null` in the stage's feature folder, called by both its Overview row and its page. Errors stay toasts.

## Permanent-change confirms

A **permanent-change confirm** (`ui/dialogs.ts`) guards a button or menu entry whose change one more action cannot reverse.

- A pure builder in the feature folder's `permanent-changes.ts` returns `PermanentChange { action, scope, cost, destructive } | null` — null when this call loses nothing.
- `confirmPermanent(change)` asks through `ConfirmService` ("Action?", scope then cost, the verb on the button) and resolves true at once for null. A flow that takes injected dialogs (the node menu's `commandFor`) maps with `permanentConfirmOptions`.
- Red (`destructive`) only when the scope deletes, discards or replaces something the user made or waited for.
- A count the confirm states comes from the rows it acts on: a selection's builder reads chapters outside the reader window first.
- A dialog the user filled in is its own confirm (Reread with split rules: red submit, body names what goes; Add titles).
- Each builder has a table spec. Read the feature folder's `permanent-changes.ts` for which of its actions confirm.
- A Book action that runs from both the Book page and the Overview (Add pauses, Reread) keeps its confirm beside its command in `pages/book/book-actions.ts` instead, so neither page imports the other's builders.
