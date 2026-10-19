# Research: how plan-then-apply edit systems shape an operation contract

Ticket: `../issues/02-operation-contract-survey.md`. Date: 2026-10-19. Branch `research/edit-operation-contract`.

## Question

How do established systems define a pluggable "operation" that an LLM (or a user) picks and fills in, and that is then
resolved, previewed and applied deterministically? Which parts of that contract recur, and where does each land in this
repo's Edit with AI stages: planner → `ScopeResolver` → `BookEditProposalService` → review rows →
`ApplyBookEditsCommand` / `BookMutations.CommitAsync`?

## Short answer

Every system surveyed splits an operation into the same six parts. The split between the **operation**, which is
registered code, and the **edit**, which is inert data produced by that code, is the strongest common pattern.

1. **Identity + model/user-facing description**: a stable name, a display title and a "when to use" description.
2. **Argument schema**: a JSON-Schema-like shape for the operation's inputs. The model or user fills it in, and code
   validates it.
3. **Target resolution / applicability**: code, not the model, decides where the operation applies (preconditions,
   visitors, scopes).
4. **A computed change set that is data**: the operation does not mutate. It returns a description of changes, and the
   host previews and applies that description. Creations are their own entry kind with no "old" side.
5. **Staleness precondition at apply**: the change set records what it was computed against (a version, a solution
   snapshot, or a "test" value). Apply refuses or degrades when that no longer holds. Atomicity and failure handling
   are explicit.
6. **Registration**: a static, discoverable registry keyed by name, with duplicate names rejected.

For this repo, the planner should not choose among tools in a tool-calling loop. It should get one composed grammar
whose top level is a tagged union over registered operations, each contributing `name + description + argument schema`.
Each operation then contributes `Resolve → Propose(rows) → Stage(accepted rows into one BookMutation)`. Rows should
carry a **kind** (`modify` with old/new, `create` with anchor + placement + new and no old) and a **precondition**
(the old value, or the anchor's identity and neighbour) that apply re-checks inside the mutation transaction. Today's
apply has no such check (see §3.5).

## 1. Sources surveyed

| System | What it contributes | Source |
|---|---|---|
| OpenAI function calling | name / description / parameters / strict, tool_choice | [OpenAI function calling guide][oai] |
| llama.cpp server | tool calls via `--jinja`; `response_format`/`json_schema` → grammar; converter limits | [function-calling.md][lcpp-fc], [server README][lcpp-srv], [grammars README][lcpp-gram] |
| MCP tools | name / title / description / inputSchema / outputSchema / annotations; human in the loop | [MCP tools spec 2025-06-18][mcp] |
| LSP `WorkspaceEdit` + code actions | edits as data; create/rename/delete resource ops; versioned docs; change annotations with `needsConfirmation`; failure handling; lazy resolve | [LSP 3.17 spec][lsp], [TextDocumentEdit][lsp-tde], [VersionedTextDocumentIdentifier][lsp-vtdi] |
| Roslyn code actions / Fix All | `CodeAction` returns a changed `Solution`; preview operations; `TryApplyChanges` refuses stale solutions; batch fixer drops overlapping fixes | [CodeAction][rcA], [ApplyChangesOperation][rACO], [Workspace.TryApplyChanges][rTAC], [FixAllProvider.md][rFix] |
| OpenRewrite recipes | name/displayName/description, `@Option` args, preconditions, visitors, scan → generate → edit, results as diffs, declarative composition | [Recipes][orw], [Recipe conventions][orw-conv] |
| jscodeshift | transform module = function(fileInfo, api, options); return-value semantics (changed / same / nothing); `--dry`, `--print`; ok/skipped/unmodified summary | [jscodeshift README][jcs] |
| JSON Patch (RFC 6902) | `add` at an array index / `-` for append; `test` precondition; whole-patch failure | [RFC 6902][rfc6902] |
| VS Code commands | registry keyed by unique id; duplicate registration is an error | [VS Code API `commands.registerCommand`][vsc] |

## 2. The recurring contract parts

### 2.1 Identity and the model-facing description

- **OpenAI**: a function is `type`, `name`, `description` ("Details on when and how to use the function"), `parameters`
  and `strict`. Guidance: "Write clear and detailed function names, parameter descriptions, and instructions", and
  "Use the system prompt to describe when (and when not) to use each function." Keep the set small: "Keep the number
  of initially available functions small for higher accuracy", aiming "for fewer than 20 functions" ([oai]).
- **MCP**: a tool has `name` ("Unique identifier"), an optional display `title`, `description`, `inputSchema`, an
  optional `outputSchema`, and `annotations` describing behaviour ([mcp]).
- **OpenRewrite**: recipes document "the name (via `Recipe.getDisplayName`) and its description (via
  `Recipe.getDescription`)" ([orw]).
- **Roslyn**: `CodeAction.Title` is "A short title describing the action that may appear in a menu", and
  `EquivalenceKey` groups equivalent actions for Fix All ([rcA], [rFix]).

**Recurring part**: a stable machine id, a short human title, and a "when (and when not) to use" text. The *when not*
part matters. OpenAI puts it in the system prompt rather than the schema. In this repo it belongs in the operation's
**prompt fragment**, next to the existing `unsupported` and "multiple operations" escape answers.

### 2.2 Argument schema

- **OpenAI strict mode**: "`additionalProperties` must be set to `false` for each object", "All fields in `properties`
  must be marked as `required`", and optional fields use "`null` as a `type` option" ([oai]). This is the same
  convention `EditProgramSchema.JsonSchema` already follows (`["string","null"]`, every property required).
- **llama.cpp**: `response_format` "supports both plain JSON output" and schema-constrained JSON, and `json_schema` is
  "a JSON schema for grammar-based sampling" ([lcpp-srv]). The converter has limits that shape a union design:
  "Can't mix `properties` w/ `anyOf` / `oneOf` in the same type", "Nested `$ref`s are broken", "Unsupported features
  are skipped silently", and `additionalProperties` "defaults to `false`" ([lcpp-gram]). The grammar constraint "does
  not apply to tool calling, where schemas are injected into the prompt" ([lcpp-gram]). That fits the map's decision
  that a raw grammar, not tool calls, is the guaranteed route.
- **OpenRewrite**: arguments are fields annotated with `org.openrewrite.Option` (displayName, description) ([orw]).
  Conventions: "Recipe/Visitors must not make changes if a field they need is null" ([orw-conv]).
- **jscodeshift**: "All options are also passed to the transformer", so the CLI does not validate them ([jcs]).

**Recurring part**: a per-operation schema for arguments. Model-facing systems make it strict and closed. Code-mod
systems also validate in code, because a schema cannot express cross-field rules. In this repo, `EditProgramParser`
already does that second step.

### 2.3 Target resolution and applicability

- **OpenRewrite**: "a recipe delegates to a visitor which handles the LST traversal and manipulation". Preconditions
  state "when it should be run", because "Most recipes are not universally applicable" ([orw], [orw-conv]). A
  `ScanningRecipe` has a scan phase "that collects information while making no new code changes", an optional
  generate phase "where new files are created", and then an edit phase ([orw]).
- **Roslyn Fix All**: one fix is applied "to all instances of 'D' across a broader scope". Scopes are document,
  project and solution, and the provider declares which scopes it supports ([rFix]).
- **LSP**: `textDocument/prepareRename` validates applicability at a position before the edit is computed. A `null`
  answer means the rename is not valid there ([lsp]). `CodeAction.disabled` gives a "Human readable description of
  why the code action is currently disabled" ([lsp]).

**Recurring part**: the *operation* owns "where do I apply". The model supplies at most a selector (a scope plus
filters) and never the resolved list. A "not applicable here, because…" answer is a first-class result.
`ScopeResolver` + `NodeFilter`/`ParagraphFilter` + the planner's `NoTargets` already play this role. Insertion adds
**anchor resolution**: a selector that yields anchors plus a placement rather than values to rewrite.

### 2.4 The change set as data, and the preview

- **Roslyn**: `GetOperationsAsync` returns "The sequence of operations that define the code action", and
  `GetPreviewOperationsAsync` returns "The sequence of operations used to construct a preview". These may differ via
  `ComputePreviewOperationsAsync` ([rcA]). The applying operation is `ApplyChangesOperation(Solution)`, and "may
  return at most one" of them per action ([rACO]). `CodeAction.Create(...)` takes a deferred
  `Func<CancellationToken, Task<Solution>>` "when the change is expensive to compute and should be deferred until
  requested" ([rcA]).
- **LSP**: a code action carries `edit` (data) and/or `command`. "If both are supplied, the `edit` is applied first,
  then the `command` is executed." `codeAction/resolve` lets the server compute `edit` lazily ([lsp]). A
  `WorkspaceEdit` holds `documentChanges`, an ordered list of `TextDocumentEdit | CreateFile | RenameFile |
  DeleteFile`, plus `changeAnnotations`. "If resource operations are present clients need to execute the operations
  in the order in which they are provided", and text edits "must be non overlapping" ([lsp], [lsp-tde]).
- **Creations in LSP** are their own kind (`CreateFile` with `overwrite` / `ignoreIfExists`). They are not a text edit
  with an empty old side. Content for a new file arrives as a following `TextDocumentEdit` against it ([lsp]).
- **Change annotations**: `label` is "A human-readable string describing the actual change", `needsConfirmation` is
  "A flag which indicates that user confirmation is needed", and `description` is rendered less prominently. Clients
  may group "edits with equal labels into tree nodes" (`groupsOnLabel`) ([lsp]).
- **OpenRewrite** reports results per source file, with `diff()` producing "A git-style diff" ([orw]). **jscodeshift**
  has `--dry` ("no changes are made to files") and `--print`. A transform's return value is the whole protocol: a
  different string means ok, the same string means unmodified, and nothing means skipped. The run ends with
  "Results: 0 errors 2 unmodified 3 skipped 5 ok" ([jcs]).
- **RFC 6902**: `add` with an array index inserts "at the specified index", and `-` appends ([rfc6902]). This is
  positional creation with no old value.

**Recurring part**: the operation returns *inert, reviewable data*, and the host renders preview and apply from it.
Creations are a distinct entry kind identified by **where** they go (path, index, or anchor + placement), not by an
existing id. Each entry carries a label so a reviewer can group and confirm. Per-entry outcomes are counted
separately: changed, unmodified (no-op), skipped (not applicable), and error. That maps onto this repo's
`ProposalStatus { Proposed, NoChange, Failed }`.

### 2.5 Conflict and staleness at apply

- **LSP versions**: a `TextDocumentEdit` names its document with an `OptionalVersionedTextDocumentIdentifier`, so the
  client can check the version before applying. The edits move the document from that version to the next
  ([lsp-tde]). The version increases after every change. `null` means "the content on disk is the master" ([lsp-vtdi]).
  Servers may answer `ContentModified` when a state change invalidates a request ([lsp]).
- **LSP failure handling** is a client capability: `abort`, `transactional` (all or nothing), `textOnlyTransactional`
  or `undo`. `ApplyWorkspaceEditResult` reports `applied`, an optional `failureReason` and the index of the
  `failedChange` ([lsp]).
- **Roslyn**: `Workspace.TryApplyChanges(newSolution)`: "If it is not, or the workspace has been updated since the
  solution was obtained from the workspace, then this method returns false" ([rTAC]). The change is computed against
  an immutable snapshot and refused wholesale if the snapshot is no longer current.
- **Roslyn Fix All merging**: the batch fixer keeps only fixes "whose fix spans don't overlap the fix spans of prior
  merged code actions", and warns that batching "may produce incorrect results" when fixes change the same region
  differently ([rFix]).
- **RFC 6902**: `test` "tests that a value at the target location is equal to a specified value". If any operation
  fails, evaluation "SHOULD terminate" and "application of the entire patch document SHALL NOT be deemed successful"
  ([rfc6902]). This is a per-entry precondition, and the whole document is atomic.

**Recurring part**: every change set records what it was computed against, at whole-document granularity (LSP
version, Roslyn snapshot) or per entry (JSON Patch `test`). Apply checks it inside the atomic unit, and the policy for
a failed check (abort all, skip entry, report index) is explicit. Multi-site operations need an overlap rule. For
creations, the precondition is about the **anchor** (it still exists, it is still where it was), because there is no
old value to compare.

### 2.6 Registration

- **VS Code**: `registerCommand(command, callback)` registers under "A unique identifier for the command", and
  "Registering a command with an existing command identifier twice will cause an error" ([vsc]).
- **MCP**: tools are discovered through `tools/list`. Every tool is "uniquely identified by a name" ([mcp]).
- **OpenRewrite**: declarative YAML recipes are loaded into the `Environment`, and a declarative recipe "composes and
  configures other recipes" via a `recipeList` ([orw]).
- **OpenAI**: `tool_choice` can force "exactly one specific function" or restrict to a subset (`allowed_tools`), and
  `parallel_tool_calls: false` prevents several calls in one turn ([oai]). This is the hosted-API equivalent of "one
  operation per plan".

**Recurring part**: a flat registry keyed by a unique name, with a startup failure on duplicates, enumerated to build
both the model-facing menu and the UI. In .NET that is `IEnumerable<IEditOperation>` from DI plus a uniqueness check
at startup, the usual DI form of a command-pattern registry.

### 2.7 Safety and human confirmation

- **MCP**: there "SHOULD always be a human in the loop with the ability to deny tool invocations", and clients should
  "Present confirmation prompts to the user for operations" ([mcp]). Annotations are hints and "MUST" be treated as
  untrusted unless they come from trusted servers ([mcp]).
- **LSP**: `needsConfirmation` per change annotation ([lsp]).
- **OpenRewrite**: "We favor making fewer changes over making wrong changes", and a recipe "should make no changes
  rather than making a potentially wrong change" ([orw-conv]). It also requires idempotence: "A recipe given the same
  LST and configuration should always produce the same results" ([orw-conv]).

## 3. Mapping to this repo's Edit with AI stages

### 3.1 What exists today (read on `main` at 59dbf3e)

- **Planner** (`BookEditPlanner`): one call to `ILlmCompletionRunner.RunAsync<EditProgram>` with
  `EditProgramSchema.JsonSchema` as `response_format`. The schema is one flat object: `reasoning` first, then
  `supported`/`unsupported_reason`, `target` (enum), `node_filter`, `paragraph_filter` and `transform` (`kind` enum +
  a superset of nullable args). The prompt carries the outline and a JSON example.
- **Resolution** (`ScopeResolver.ResolveAsync`) yields `EditTarget`s with `CurrentValue` and `OrdinalInScope`. Zero
  targets answers `NoTargets`.
- **Proposal** (`BookEditProposalService`): deterministic transforms are a `switch` on `TransformKind`. The LLM
  transform runs in batches of 8 with a second grammar. Rows are `ProposedEdit(Kind, Id, DisplayPath, OldValue,
  NewValue, Status, FailureReason)`. `ProposeOneAsync` re-asks one row with a hint, for LLM plans only.
- **Session** (`BookEditSessionStore`) keeps the plan and targets server-side under an opaque id (2 h sliding).
- **Apply**: `ApplyBookEditsCommand(FolderId, IReadOnlyList<BookEditItem(Kind, Id, NewValue)>)` →
  `ApplyBookEditsMutationImplementation`, one Book mutation. A missing target is **skipped** (`if (item is null)
  continue;`), and unchanged text is a no-op.
- **Insertion already exists as a command**: `InsertNodeCommand(FolderId, AnchorKind, AnchorId, Placement{Before,
  After, AtStart, AtEnd}, Title?, Text?)`. That is the LSP `CreateFile` / JSON Patch `add` analogue: a creation
  addressed by anchor + placement.

### 3.2 Strawman contract

This is a sketch for ticket 03 to grill, not a decision.

```csharp
// Registered as IEditOperation in DI; names unique (startup throws on duplicates, as VS Code does).
interface IEditOperation
{
    string Name { get; }                 // grammar tag, e.g. "insert_node", "regex_replace"
    string Title { get; }                // UI label in the review dialog / API docs
    string PromptFragment { get; }       // when to use AND when not to; one worked example
    string ArgumentSchema { get; }       // closed JSON-Schema object: all required, nullable optionals
                                         // (strict-mode convention, llama.cpp-safe: no $ref, no anyOf+properties)
    EditOperationArgs? Parse(JsonElement args, out string? error);   // cross-field validation in code

    // Target resolution: code decides where (Roslyn FixAll scope / OpenRewrite precondition).
    Task<IReadOnlyList<EditSite>> ResolveAsync(ProjectFolderId f, EditOperationArgs a, CancellationToken ct);

    // Proposal: inert rows only, never a DB write (Roslyn ApplyChangesOperation / LSP WorkspaceEdit).
    IAsyncEnumerable<ReviewRow> ProposeAsync(ProjectFolderId f, EditOperationArgs a,
        IReadOnlyList<EditSite> sites, ProposeContext ctx, CancellationToken ct);
    Task<ReviewRow>? ProposeOneAsync(...);   // null => no "Ask AI again" (today: deterministic plans)

    // Staging: accepted rows become effects inside the one BookMutation transaction.
    Task<StageResult> StageAsync(ProjectDbContext db, IReadOnlyList<ReviewRow> accepted, CancellationToken ct);
}

abstract record ReviewRow(string OperationName, string RowId, string Label /* LSP change-annotation label */,
                          RowStatus Status /* Proposed | NoChange | Failed */, string? FailureReason);
record ModifyRow(..., BookEditTargetKind Kind, Guid TargetId, string OldValue, string NewValue) : ReviewRow; // precondition = OldValue
record CreateRow(..., NodeKind AnchorKind, Guid AnchorId, Placement Placement,
                 string? AnchorPreview /* neighbour text shown as context */, string? Title, string? Text,
                 Guid? AnchorNeighbourId /* precondition: who sits next to the anchor at plan time */) : ReviewRow;
```

| Recurring part | Where it lands in this repo | Change from today |
|---|---|---|
| Identity + description (§2.1) | `Name`, `Title`, `PromptFragment`. The planner prompt is the outline plus the joined fragments plus the shared "unsupported / multiple operations" rules | The `transform.kind` enum becomes the operation tag. The prompt's per-transform paragraphs move into fragments |
| Argument schema (§2.2) | The planner grammar is a **tagged union**: `{reasoning, supported, unsupported_reason, operation: <enum of names>, args: <…>}`. Ticket 01 decides how to express the per-operation `args` under llama.cpp's "no `properties` beside `anyOf`" rule (for example one `anyOf` of closed objects, each with a `const` `operation`) | `EditProgramSchema` becomes one fragment per operation. `EditProgramParser` validation is split per operation |
| Target resolution (§2.3) | `ResolveAsync`. Text operations reuse `ScopeResolver` + `NodeFilter`/`ParagraphFilter` unchanged. `insert_node` resolves **anchors** (kind + id + placement) with the same filters. Zero sites gives `NoTargets`, the LSP `disabled`/`prepareRename null` analogue | New site type for anchors. `EditTarget` stays for modify operations |
| Change set as data, preview (§2.4) | `ProposeAsync` yields `ModifyRow` or `CreateRow`. The dialog renders a modify row as old → new and a create row as "＋ new Chapter 'Epilogue' **after** Chapter 53 'The Merchant Princes'" with no old column. `Label` groups rows (LSP `groupsOnLabel`). Deferred and expensive work (LLM text) stays lazy and per row, like Roslyn's deferred `Create(...)` and LSP `codeAction/resolve` | `ProposedEdit` (which assumes `Id + OldValue`) becomes a row union. The review-row shapes are ticket 05 |
| Staleness at apply (§2.5) | `StageAsync` runs inside `BookMutations.CommitAsync`'s transaction. A **modify** row re-reads the target and compares `OldValue` (RFC 6902 `test`). A **create** row checks that the anchor exists and, if recorded, that the neighbour is unchanged. The policy is one of: refuse the whole mutation (Roslyn / LSP `transactional`), or skip that row and report it (today's skip, made visible as LSP `failedChange`) | **New.** Today the apply carries only `NewValue`, so a row reviewed against stale text silently overwrites a newer hand edit. `BookRevisionSequence` cannot serve as the check, because it is "not an optimistic-concurrency column" and is process-local |
| Overlap / ordering (§2.5) | One operation per plan makes overlap mostly the operation's own concern. Creations against the same anchor + placement must apply in a defined order (LSP "in the order in which they are provided"; JSON Patch index semantics). Rule: rows with the same anchor + placement apply in row order, so "insert A after X, then B after X" yields X, A, B and not X, B, A | **New** for inserts. Modify rows on distinct ids cannot overlap |
| Registration (§2.6) | `services.AddEditOperation<T>()` → `IEnumerable<IEditOperation>`. A startup check rejects duplicate names. The registry builds the planner grammar, the prompt and the API's operation list. An architecture test keeps the advanced path out of Edit with AI (map: back-out boundary) | **New**, in the advanced path only |
| Confirmation (§2.7) | The review dialog *is* the confirmation (per-row accept). Per the user's "permanent-change confirm" rule, a filled-in dialog is its own confirm. Structural rows could carry a `NeedsConfirmation`-style flag if the dialog ever bulk-accepts | Nothing new, unless bulk accept arrives |

### 3.3 Lessons for creations, which have no old value

1. **A creation is a different entry kind, not a modify with `OldValue = ""`.** LSP models it as `CreateFile` (by
   URI), and JSON Patch as `add` (by path + index). Neither has an old side. A shared row base with a `kind`
   discriminator keeps the review UI and apply honest. An empty-string old value would make "unchanged" checks and the
   precondition meaningless.
2. **Address creations by anchor + placement, not by a new id.** The id does not exist until commit. ADR 0007
   receipts already report "any created identity and the node holding it", which is the place to learn it. This repo's
   `InsertNodeCommand` already has the right address.
3. **Give the preview its context from the neighbour.** With no old text, the diff needs the anchor's title or text
   and the placement word to be reviewable. LSP's `label`/`description` split (prominent / less prominent) is the
   model.
4. **The creation's precondition is the anchor, not a value.** The anchor still exists. Optionally, the neighbour on
   the placement side is unchanged, so that "after Chapter 3" still means between 3 and the chapter that was 4.
   Without the neighbour check, two concurrent inserts or a reorder silently change meaning.
5. **Existence collision is explicit in LSP** (`overwrite` vs `ignoreIfExists`). The book analogue is "a chapter with
   this title already exists here". It should surface as a row warning at propose time, not be decided silently at
   apply.
6. **Multiple creations at one anchor need a defined order.** LSP executes resource operations in order. Rows should
   apply in row order, with each later row anchored after the previous one's created node, or all against the original
   anchor with an explicit sequence.

### 3.4 Lessons for staleness

1. **Check what the reviewer saw, inside the transaction.** Roslyn refuses the whole change if the workspace moved.
   LSP checks per-document versions. JSON Patch checks per-entry `test` values. The finest-grained check this repo can
   do cheaply is per row: compare the persisted value with the row's `OldValue`. That needs no schema change, and it
   fits ADR 0007's single commit point.
2. **Choose and state the failure policy.** The options are: all-or-nothing (Roslyn, LSP `transactional`, RFC 6902),
   or skip the stale row and report it (LSP `failedChange`, jscodeshift's "skipped" count). Today a missing target is
   skipped silently. The advanced path should at least *report* skipped or stale rows to the dialog. A good default is
   all-or-nothing for structural rows, because a partial insert set is hard to reason about, and skip-and-report for
   independent text rows.
3. **Session lifetime is a staleness window.** The plan session lives 2 h. LSP's `ContentModified` and Roslyn's
   snapshot model both treat "computed against an old state" as normal and cheap to detect. Re-check at apply rather
   than trying to lock.
4. **Do not reuse the receipt revision as the version.** `BookRevisionSequence` restarts at 1 with the process, and its
   doc comment says it is not an optimistic-concurrency column. It is fine as an "anything changed since plan?" hint
   for the UI, but not as the guard.

### 3.5 Gaps today that the advanced path should not inherit

- `BookEditItem(Kind, Id, NewValue)` has no old value, so apply cannot detect that a row went stale between review and
  apply (`ApplyBookEditsMutationImplementation` compares only against `NewValue`, to detect "unchanged").
- `ProposedEdit` requires `Id` and `OldValue`. It cannot carry a creation. This matches the seven "existing target"
  assumptions listed in the absorbed ai-structural-edits research (`../completed/ai-structural-edits/issues/01-edit-with-ai-pipeline-shape.md`).
- The deterministic/LLM split is a `switch` on `TransformKind` inside the proposal service. Under the contract, it
  becomes a property of each operation (whether `ProposeOneAsync` is offered).

## 4. What not to copy

- **Tool-calling loops** (OpenAI `auto`/`required`, MCP call → result → model). The map excludes them. llama.cpp's
  grammar constraint "does not apply to tool calling" ([lcpp-gram]), and parallel tool calls are "disabled by
  default" ([lcpp-fc]). The useful part of tool definitions is the *shape* (name + description + strict schema), not
  the protocol.
- **Lazy edit resolution across a network** (LSP `codeAction/resolve`). Here the analogue is just the existing
  propose/progress run.
- **Fix All's "drop overlapping fixes" merge**. One operation per plan with distinct target ids makes it unnecessary
  for modify rows. Insert rows need the ordering rule in §3.3.6 instead.

## Sources

- [oai]: OpenAI, *Function calling* guide, <https://developers.openai.com/api/docs/guides/function-calling> (redirected from platform.openai.com).
- [lcpp-fc]: ggml-org/llama.cpp, `docs/function-calling.md`, <https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md>.
- [lcpp-srv]: ggml-org/llama.cpp, `tools/server/README.md`, <https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md>.
- [lcpp-gram]: ggml-org/llama.cpp, `grammars/README.md` (JSON Schema → grammar), <https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md>.
- [mcp]: Model Context Protocol, *Tools*, spec 2025-06-18, <https://modelcontextprotocol.io/specification/2025-06-18/server/tools>.
- [lsp]: Microsoft, *Language Server Protocol Specification 3.17*, <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/> (WorkspaceEdit, resource operations, ChangeAnnotation, FailureHandlingKind, CodeAction, codeAction/resolve, workspace/applyEdit, prepareRename).
- [lsp-tde]: LSP 3.17 `TextDocumentEdit` type, <https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/types/textDocumentEdit.md>.
- [lsp-vtdi]: LSP 3.17 `VersionedTextDocumentIdentifier` type, <https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/types/versionedTextDocumentIdentifier.md>.
- [rcA]: Microsoft Learn, `Microsoft.CodeAnalysis.CodeActions.CodeAction`, <https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.codeactions.codeaction>.
- [rACO]: Microsoft Learn, `ApplyChangesOperation`, <https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.codeactions.applychangesoperation>.
- [rTAC]: Microsoft Learn, `Workspace.TryApplyChanges(Solution)`, <https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.workspace.tryapplychanges>.
- [rFix]: dotnet/roslyn, `docs/analyzers/FixAllProvider.md`, <https://github.com/dotnet/roslyn/blob/main/docs/analyzers/FixAllProvider.md>.
- [orw]: OpenRewrite, *Recipes*, <https://docs.openrewrite.org/concepts-and-explanations/recipes>.
- [orw-conv]: OpenRewrite, *Recipe conventions and best practices*, <https://docs.openrewrite.org/authoring-recipes/recipe-conventions-and-best-practices>.
- [jcs]: facebook/jscodeshift, `README.md`, <https://github.com/facebook/jscodeshift/blob/main/README.md>.
- [rfc6902]: IETF RFC 6902, *JavaScript Object Notation (JSON) Patch*, <https://www.rfc-editor.org/rfc/rfc6902> (§4.1 add, §4.6 test, §5 error handling).
- [vsc]: Microsoft, *VS Code API*, `commands.registerCommand`, <https://code.visualstudio.com/api/references/vscode-api>.
- Repo (main @ 59dbf3e): `src/Read2Me.Services/BookEdits/{EditProgram,BookEditPlanner,BookEditProposalService}.cs`,
  `src/Read2Me.App/Api/BookEditEndpoints.cs`, `src/Read2Me.Core/Models/BookCommands.cs` (`ApplyBookEditsCommand`,
  `InsertNodeCommand`, `Placement`), `src/Read2Me.Services/Mutations/Implementations/BookEditMutationImplementations.cs`,
  `src/Read2Me.Services/Mutations/BookRevisionSequence.cs`, `docs/adr/0007-book-mutations-reconcile-book-view-projections.md`.

### Source caveats

- VS Code's `WorkspaceEdit` / `WorkspaceEditEntryMetadata.needsConfirmation` API page was too long to fetch to the
  relevant section, so the confirmation point is cited from the LSP `ChangeAnnotation` instead.
- The llama.cpp docs do not say whether tool-call output is grammar-constrained in the server. The grammars README
  says the JSON-schema constraint "does not apply to tool calling". The map's raw-grammar decision stands either way.
- The LSP spec text on how a client reacts to a version mismatch was not found verbatim. The check is described in
  the `TextDocumentEdit` type ("the client can check the version"), and the behaviour is left to the client.
