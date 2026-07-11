# 01 — Chain split: attribution chain becomes a standalone stored list

**Status:** Done

## Parent

`src/Issues/character-discovery-prd.md` (Character Discovery PRD)

## What to build

The attribution escalation chain becomes a plain ordered list of LLM server config IDs that includes its own first step. `ActiveLlmConfigId` reverts to meaning exactly one thing: the default, general-purpose LLM used by voice-prompt generation, AI book edits, container warm-up and the settings test panel. Nothing prepends the active config to the chain any more.

A user who nominates a large model as default and builds the chain as `[small, large]` gets: attribution runs small-first, while voice prompts and AI book edits always hit the large model. A user with one config who never touches the chain panel loses nothing — an empty chain falls back to the active config as a single step.

Details, all from the PRD's implementation decisions:

- The app-settings entity's escalation column is renamed to hold the **whole** attribution chain (JSON array of config IDs, index 0 first). The escalation migration never shipped, so it is edited in place rather than superseded by a rename migration. Local dev databases must be recreated or hand-patched — note this in the commit message.
- The LLM settings service surface is renamed: get/set now operate on the full attribution chain, and the resolved-chain getter returns the stored list with **no active prepend**. Fallback rule lives inside the service: stored chain resolves to ≥1 configs → return them in order; zero configs but an active config exists → return `[active]`; otherwise empty (callers already map empty to no-LLM-configured).
- Retained behaviour: lazy prune of dangling IDs on read (re-saving pruned list), eager prune on config delete (including index 0 — the chain shortens, nothing is promoted), dedupe by ID, corrupted JSON deserialises to an empty list.
- The attribution service changes by exactly one call-site rename. Chain-walk, single-step short-circuit, per-step prompt style/batch size and self-consistency are untouched.
- The self-consistency setting is unchanged.

## Acceptance criteria

- [x] App-settings entity stores the full attribution chain under the renamed column; the in-place-edited migration applies cleanly on a fresh database.
- [x] Resolved attribution chain no longer prepends the active config.
- [x] Empty stored chain with an active config resolves to `[active]`; empty chain with no active config resolves to empty.
- [x] Chain containing a deleted/dangling config ID prunes on read and re-saves the pruned list.
- [x] Deleting an LLM config removes it from the chain, including at index 0, without promoting anything else.
- [x] Corrupted chain JSON degrades to an empty chain (fallback applies) rather than throwing.
- [x] Attribution service consumes the chain via the renamed getter; existing attribution-chain tests pass unchanged after the rename — that is the regression signal that chain consumption did not change.
- [x] All non-attribution LLM callers (voice prompts, book edits, warm-up, test panel) still resolve the active config unchanged.
- [x] Settings-service tests cover every resolution rule above against a real in-memory/SQLite context, following the existing LLM settings service tests.

## Blocked by

None — can start immediately.
