# Messages to a new user

The partials live in `ui/partials.ts`, `ui/menu.ts` and `ui/dialogs.ts`; the wording and the rules for when each shows live in the feature folder, as pure functions with a table spec.

## Advice

Advice comes before the click, never inside a confirm: an out-of-order action shows its one sentence through `adviceLine` (under the action, above a dialog's own button) or a menu entry's `hint`.

## Blocked reasons

A long-lived disabled control carries a **blocked reason** through `blockable`. The reason is a pure `xBlockedReason(gate): string | null` in the feature folder's logic file (`editor-logic.ts`'s `applyBlockedReason`), never in `ui/`. A setting a step needs is named in prose in the blocked reason, plus a callout with an `href` action where a link matters.

The Book page's book writes share one edit gate instead: `BookEditor.blocked(local?)` answers `Block | null` (`{ reason: null }` blocks without a reason, e.g. while saving) over the pure `blockedReason` in `api/step-view.ts` (viewing a step > stale > the caller's local reason > none), and `BookEditor` declines a write on the same answer.

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
