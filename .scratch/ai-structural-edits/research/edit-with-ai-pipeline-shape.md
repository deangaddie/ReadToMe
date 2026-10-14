# Edit with AI pipeline shape — what a structural operation would need

Research for `issues/01-edit-with-ai-pipeline-shape.md`. Facts only, read from code at `db18076` (main, 2026-10-14). Paths are relative to `src/` unless noted.

## The pipeline today, end to end

`POST …/book-edits/plan` → planner (LLM, one call) → `ScopeResolver` (pure traversal) → session stored → `POST …/propose` → `BookEditProposalService` (code or batched LLM) → rows on hub / `GET` → web review (`ReviewSelection`) → `ApplyBookEdits` command → `ApplyBookEditsMutation` via `BookMutations.CommitAsync`.

Wiring: `Read2Me.App/Api/BookEditEndpoints.cs:49-60` (routes), `:63-94` (plan = planner + resolver + session create), `Read2Me.App/Live/BookEditRunCoordinator.cs:19-28` (propose run).

## Stage by stage

### 1. Planner grammar / `EditProgram`

- **One program = one target selector + one transform.** `EditProgram(Supported, UnsupportedReason, Target, NodeFilter, ParagraphFilter, Transform, Reasoning)` — `Read2Me.Services/BookEdits/EditProgram.cs:50-57`. It is a single object, not a list of operations.
- Target enum is value kinds only: `VolumeTitle, PartTitle, ChapterTitle, ParagraphText` — `EditProgram.cs:3`; JSON schema enum `EditProgram.cs:91`. Transforms rewrite a string: `RegexReplace, SetTemplate, Llm, ChangeCase` — `EditProgram.cs:5`, schema `:121-132`.
- The schema is sent as `response_format` json_schema (grammar-constrained) — `EditProgram.cs:79-84`, `BookEditPlanner.cs:41-44`; it requires every top-level field (`:134`), so any new operation kind has to fit this object or the schema changes shape.
- Addressing is **filters, not positions**: `NodeFilter(OrdinalFrom, OrdinalTo, TitleRegex)` over book-wide 1-based ordinals at the target level (`EditProgram.cs:13-19`); `ParagraphFilter` predicates over paragraph/item ordinals and text (`:21-38`). There is no field for "before/after/at start/at end of X" or for new content.
- The prompt refuses structure explicitly: "If the instruction asks for anything other than editing titles or paragraph text — adding, deleting, splitting, merging or reordering content … set "supported" to false" — `Read2Me.Services/Llm/PromptTemplates.cs:521-523`. Web message for `Unsupported` says "AI edits can change titles and paragraph text only." — `Read2Me.Web/src/app/pages/book/edit-with-ai/review-model.ts:196`; `docs/agents/api.md:445` says the same.
- What the planner sees of the book: `ChapterOutlineBuilder` — counts plus at most 20 chapter titles (`Read2Me.Services/Llm/ChapterOutlineBuilder.cs:13-41`). No node ids, no Part/Volume titles, no paragraph text. A planner addressing "after chapter 3" or "into Part 2" would have only chapter ordinals/titles (first 20) to go on.
- Parser maps strings to enums with closed switches (`EditProgramParser.cs:159-196`) and validates per transform kind (`:97-124`); unsupported short-circuits to a dummy `ChapterTitle` program (`:72-83`). A new op kind needs a new mapping + validation branch.
- Describer (plan summary) switches on target + transform (`EditProgramDescriber.cs:6-17`).

### 2. `ScopeResolver`

- Resolves to `EditTarget(Kind, Id, CurrentValue, DisplayPath, OrdinalInScope, ChapterId?, ParagraphId?)` — `ScopeResolver.cs:10-17`. Every target is an **existing entity with a current string value**.
- Walks Volume → Part → Chapter → Paragraph in reading order, emitting one target per matched node/item (`ScopeResolver.cs:38-109`). Titles: `AddTitleTarget` (`:146-155`); paragraph text: one target per **content item** (`:157-183`), pause items and blank text excluded (`ContentItems`, `:210-215`) — so empty containers and pause paragraphs never become targets, and a chapter with no items yields nothing.
- Regex-pruning transforms keep only matching targets (`:42`, `:143-144`); `targets.Count == 0` → plan answers `NoTargets` (`BookEditEndpoints.cs:77-79`).
- For an insert, the resolved thing would be an **anchor + position** (an existing node and where relative to it) rather than a target to rewrite; `EditTarget` has no position field and its `Id` is the thing changed. The traversal and ordinal counting are reusable for finding anchors (it already computes book-wide volume/part/chapter ordinals and per-chapter paragraph ordinals).

### 3. Session store

- Session holds `Program` + resolved `Targets` (`BookEditSessionStore.cs:31-47`). `FindTarget(Guid id)` assumes "a program has one target selector, so within a session an id names at most one target" (`:52-55`) — used by propose-one (`BookEditEndpoints.cs:139-141`). A proposed **new** node has no id until created; a mixed batch could give two rows one anchor id.

### 4. `BookEditProposalService` / `ProposedEdit`

- `ProposedEdit(Kind, Id, DisplayPath, OldValue, NewValue?, Status, FailureReason)` — `BookEditProposalService.cs:11-21`. Status: `Proposed | NoChange | Failed` (`:9`). `NoChange` is computed as `newValue == target.CurrentValue` (`Build`, `:246-254`) — meaningless for an insert (no old value).
- Deterministic path computes one new string per target (`:65-95`); LLM path batches 8 targets per call with a schema of `{index, reasoning, new_text}` (`EditProgram.cs:139-162`, `BookEditProposalService.cs:97-157`). Both are "target → new text". Inserted content (title of a new node, first item text of a new paragraph) could fit as a NewValue, but position/parent has no slot.
- `ProposeOneAsync` (per-row retry) is LLM-only and keyed by an existing target (`:164-202`).
- `ToEditItem()` (`:20`) has no production callers (grep: only its definition).
- Wire row: `BookEditRowDto(Kind, Id, DisplayPath, OldValue, NewValue, Status, FailureReason)` — `Read2Me.App/Live/LiveMessages.cs:200-205`; TS `BookEditRow` — `Read2Me.Web/src/app/api/dtos.ts:1117-1125`.

### 5. Web review rows

- `ReviewRow = { proposal, userValue }`; states `proposed | noChange | failed | edited | invalid` — `review-model.ts:7-30`, `rowState` `:40-54`. `invalid` = emptied value, "V1 has no deletes" (`:16-17`); dialog says "AI edits never delete text." (`edit-with-ai-dialog.ts:389`). An untitled new container (title optional per Node insertion) would hit `invalid` under these rules.
- Selection keyed by `proposal.id`, with the comment "A program has one target selector, so an id names at most one row" — `review-model.ts:79-83`; replace/edit by id (`:175-177`).
- Row UI is old-vs-new text: `displayPath`, `oldValue` preview, new value, a "Current" paragraph and a "Proposed" textarea — `edit-with-ai-dialog.ts:323-390`. No placement display.
- `toEdits()` emits `{kind, id, newValue}` only — `review-model.ts:148-156`. Apply sends one `ApplyBookEdits` via `editor.tryExecute` — `edit-with-ai-dialog.ts:759-773`.

### 6. `ApplyBookEditsCommand` → mutation

- Wire: `BookEditTargetKind { VolumeTitle, PartTitle, ChapterTitle, ParagraphItemText }`, `BookEditItem(Kind, Id, NewValue)`, `ApplyBookEditsCommand(FolderId, Edits)` — `Read2Me.Core/Models/BookCommands.cs:154-156`; TS mirror `Read2Me.Web/src/app/api/book-commands.ts:20,31-34,312-315`. The command carries no session/program id — apply is decoupled from the plan (`BookEditEndpoints.cs:37-41`; `docs/agents/api.md:474-478`), so an agent can post it directly.
- Handler: `ApplyBookEditsHandler` → `mutations.ExecuteCommandAsync(new ApplyBookEditsMutation(...))` — `Read2Me.Services/Commands/Handlers/ApplyBookEditsHandler.cs:13-14`; mutation record `Read2Me.Services/Mutations/BookMutation.cs:352-353`.
- Implementation loops edits: item text via `BookEditEffects.RewriteAsync` (clears audio + reviews), else `RetitleAsync`, which throws for any non-title kind ("Not a titled node.") — `Read2Me.Services/Mutations/Implementations/BookEditMutationImplementations.cs:267-316`, `:29-43`. Absent targets are **skipped**, not refused (`:261-264`, `:285`).
- The family's doc states the assumption outright: every mutation here rewrites content "and none of them creates, deletes or moves a node" — `BookEditMutationImplementations.cs:9-10`.
- Effects: `Scope = Exact`, facets `ItemText | Audio | Reviews | NodeTitle`, ids in `NodeIds/ParagraphIds/ParagraphItemIds` — `:303-315`. Never `Structure`, never `CreatedId`.

## Where "every proposal is a change to an existing target" is assumed

1. Grammar/target enum: value kinds only (`EditProgram.cs:3,91`); prompt refuses add/reorder (`PromptTemplates.cs:521-523`).
2. `EditTarget.Id` + `CurrentValue` — the target exists and has a value (`ScopeResolver.cs:10-17`).
3. `ProposedEdit.OldValue` non-null and `NoChange` = equal to old (`BookEditProposalService.cs:11-21,246-254`).
4. One-id-one-row in session and review (`BookEditSessionStore.cs:52-55`, `review-model.ts:81`).
5. Empty value = invalid ("no deletes") (`review-model.ts:16-17,51`).
6. `BookEditItem(Kind, Id, NewValue)` — no parent/position/new-node payload (`BookCommands.cs:155`).
7. Mutation family "never creates, deletes or moves a node"; non-title kind throws (`BookEditMutationImplementations.cs:9-10,43`); effects never carry `Structure`.

## Seams where an insert could enter

- **Program**: a new target/op kind in `EditProgram` + schema + parser + describer + prompt (`EditProgram.cs`, `EditProgramParser.cs`, `EditProgramDescriber.cs`, `PromptTemplates.cs:448+`). Live eval `Read2Me.Tests/Services/BookEdits/EditPlanLiveEvalTests.cs` renders the same prompt via `BookEditPlanner.RenderPrompt` (`BookEditPlanner.cs:69-79`).
- **Resolver**: the reading-order walk + ordinals in `ScopeResolver.ResolveAsync` is where an anchor would be found; output type would need a position (or a sibling record type).
- **Proposal**: deterministic path already exists for code-computed values (`ProposeDeterministic`); an insert whose content is given in the instruction could be computed in code; an LLM-written body would reuse the batch path only if it is "one text per row".
- **Wire/review**: `BookEditRow`/`BookEditRowDto` and `ReviewRow` are the only row shapes; `kind` is already a discriminator string on both sides.
- **Apply**: `BookEditItem`/`BookEditTargetKind` (`BookCommands.cs:154-155`) and the loop in `ApplyBookEditsMutationImplementation` (`:278-302`) — or a different mutation.

## Can apply be one Book mutation for a mixed batch?

What the mutation path allows (facts):

- `BookMutations.CommitAsync` runs **one implementation** inside one transaction, one `SaveChangesAsync`, one commit, one revision, one receipt — `Read2Me.Services/Mutations/BookMutations.cs:38-109,115-138`. Implementations "apply inside the supplied transaction … they neither save nor publish" (ADR 0007, `docs/adr/0007-*.md`, Decision). Nothing limits how many entities one implementation stages; `ApplyBookEdits` already stages many rows in one commit (`BookEditMutationImplementations.cs:255-260`).
- Structural mutations stage writes via `BookMutationApplier.StageMutation(db, HierarchyMutation)` (`Read2Me.Services/Commands/BookMutationApplier.cs:32-62`), planned against `LoadBookHierarchyAsync(db)` — which **queries the database** (`:10-25`). Staged-but-unsaved `Added` entities are not returned by EF queries, so a second insert planned in the same implementation by reloading would not see the first; sequential inserts within one mutation would have to plan against one in-memory `BookHierarchy` (or compute orders themselves, as pause-paragraph insertion does — `context/book-structure.md:10`).
- Effects can express a mixed batch: facets are flags (`BookMutationEffects.cs:20-48`), id lists are per kind (`:86-89`), `Structure` "with exact identifiers" is already used by item insert (`InsertParagraphItemMutationImplementation.cs:48-58`). `CreatedId` holds **exactly one** created thing (`BookMutationEffects.cs:74-75`); a batch creating several nodes has no list of created ids (receipts surface `CreatedId` as the command result, `BookCommandWireContract.cs:64`). `Structural` relations cover only `Split`/`Merge` (`:50-62`).
- Reader side: `receipt-plan.ts` treats `Structure` and `NodeTitle` as overview reloads (`Read2Me.Web/src/app/pages/book/receipt-plan.ts:46`); a `Structure` receipt whose ids name nothing loaded rereads the visible chapter (`:80-91`); `WholeProject` scope drops other chapters (`:76-79`); a foreign structural receipt toasts (`:99-100`). A mixed text+insert receipt would already be handled by these rules without new read-side facets.
- Rejection semantics differ: ApplyBookEdits **skips** missing targets (`BookEditMutationImplementations.cs:261-264`); manual inserts **refuse** a missing anchor with `NotFound` (`InsertParagraphItemMutationImplementation.cs:35-38`); and `ExecuteCommandAsync` answers `NotFound` as null/NoChange on the wire (`BookCommandWireContract.cs:36-38`). A mixed batch has to pick one of these per row.
- Existing node-level insert planning lives in `BookHierarchy` (`Read2Me.Services/Books/BookHierarchy.cs:175-216`, `PlanInsertParagraphItem`); fractional orders mean an insert writes only the new row (`.scratch/edit-options/issues/01-manual-node-insertion.md`, "Facts behind it"). The manual Node insertion mutations specified by the edit-options map do not exist yet.
- Architecture guard: `Read2Me.Tests/Architecture/OneConsistencyModelTests.cs:41` requires every mutation implementation to be registered for its mutation — a new or widened mutation is checked there.

So: the path supports one transaction/one receipt for many staged writes of mixed kinds; the gaps are in the payload (`BookEditItem`), the implementation's dispatch (`RetitleAsync` throws on non-title kinds), in-transaction planning of several inserts (DB-backed hierarchy load), single `CreatedId`, and the skip-vs-refuse rule.

## Notes for "move" later

- Moves today have no mutation at all (out of scope per `.scratch/edit-options/map.md`). Fractional `Order` keys mean a move would be an `Order` (+ parent id) update on one row (`ToUpdate` in `HierarchyMutation`, `BookHierarchy.cs:586-589`). Anchor + position addressing (as for insert) is what a move's destination would need; the source is an existing target, which `ScopeResolver` already produces.
