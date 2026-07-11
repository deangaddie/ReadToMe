# 02 — Chain UI: flat reorderable chain panel + settings page labels

**Status:** Done

## Parent

`src/Issues/character-discovery-prd.md` (Character Discovery PRD)

## What to build

The LLM settings page reflects the chain split from ticket 01, end-to-end in the browser.

- The escalation presenter loses its Primary / Escalation split and exposes one flat chain collection. Every row — including index 0 — is reorderable and removable like any other step. The presenter additionally exposes the active config purely so the panel can name the fallback.
- The chain panel drops its fixed read-only "primary (active)" row and its "select an active configuration above" alert. When the chain is empty it shows a hint naming which config attribution will fall back to, so the fallback is not invisible behaviour.
- The LLM settings page relabels the active chip and adds helper text stating that the default config is used for voice prompts, AI book edits and character discovery, while attribution uses the chain.

## Acceptance criteria

- [x] Presenter exposes a single flat chain; no primary/escalation distinction remains.
- [x] Index 0 can be moved down and removed through the presenter like any other row.
- [x] With an empty chain, the presenter surfaces the active config as the named fallback and the panel renders the fallback hint instead of a "select active" alert.
- [x] Settings page helper text states what the default config is used for and that attribution uses the chain.
- [x] Presenter tests (no Blazor rendering, following the existing escalation presenter tests) cover the flat chain, index-0 mobility and fallback surfacing.
- [x] The existing attribution escalation panel E2E test updated to the new panel's selectors and labels (compiles; requires live E2E env to run).

## Blocked by

- 01 — Chain split.
