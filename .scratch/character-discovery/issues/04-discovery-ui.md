# 04 — Discovery UI: button, review dialog, apply

**Status:** Done

## Parent

`src/Issues/character-discovery-prd.md` (Character Discovery PRD)

## What to build

The full user-facing discovery flow: a "Discover characters" button on the Characters tab runs pre-flight, streams the discovery request live, shows the proposed characters in a review dialog, and creates exactly the accepted ones on accept.

Details, all from the PRD's implementation decisions:

- **Pre-flight:** a new AI task kind for character discovery, mapped by the task-requirements resolver to the active LLM's base URL (same URL the attribution and voice-prompt kinds resolve to). Invoked from the Characters tab *before* the dialog opens, not from inside it — the batch runner is a singleton while the dialog service is per-circuit. The user gets the same "start the container?" prompt as other AI tasks, and a clear message when no LLM is configured.
- **Dialog, two phases** (no instruct phase — the button is the instruction):
  - *Discovering:* indeterminate progress bar, cancel control backed by a cancellation token, collapsible shared LLM stream view.
  - *Review:* each proposed character binds to a mutable view model carrying name, alias list, an include flag, and an "already exists" flag. Exists is computed against the loaded roster using the existing character resolver's name-or-alias match, testing both the proposed name and each proposed alias. Name is editable in place; aliases use the closable-chip + inline-text-field idiom from the character detail panel; the scrolling list and select-all / select-none follow the book-edit review dialog. The dialog returns the accepted view models and performs no writes.
- **Apply — no new command, no new handler.** The accepted rows are applied by looping the existing commands: create-character is already idempotent (returns the existing ID on name-or-alias match) and add-character-alias already deduplicates. Together that gives "skip existing, create new, still enrich an existing character with new aliases" for free. The loop lives on the character presenter as a single method, following the presenter's existing execute-and-reload idiom, so it is testable without a dialog.
- The button is available even when the only character is the seeded narrator — that is exactly when discovery is most valuable.

## Acceptance criteria

- [x] "Discover characters" button on the Characters tab triggers pre-flight for the new task kind, then opens the dialog.
- [x] Discovering phase shows the live stream, and cancel aborts the request.
- [x] Review rows flag already-existing characters (matched by name or any alias against name-or-alias in the roster).
- [x] Name editable in place; aliases addable and removable; rows excludable; select-all and select-none work.
- [x] Presenter apply method: included rows produce exactly one create command each plus one add-alias command per alias; excluded rows produce nothing; a row whose only change is a new alias on an existing character still produces the alias command.
- [x] Presenter tests over a fake command handler cover the apply behaviour above; the dialog stays a thin untested mapping layer over the presenter.
- [ ] Re-running discovery on a seeded roster flags everything existing and accepting is a no-op (manual verification per PRD — not yet run).

## Blocked by

- 03 — Discovery service.
