# Book mutations reconcile immutable Book View projections

## Status

accepted

## Context

The persisted Book is authoritative, but an open Book View currently becomes coherent through caller-specific combinations of local entity patches, selection clearing, Node Status reseeding, cache invalidation, targeted reloads and full reloads. Those rules are spread across the presenter, Razor modules, mutation handlers and queue events. As a result, the same committed change can reconcile the initiating view while leaving another circuit or a command-endpoint caller's open view stale.

## Decision

A **Book mutation** is one user/domain operation, even when it changes several persisted records. Every mutation producer—including Book View gestures, the command endpoint, attribution and audio queues, imports, AI edits, Character lifecycle changes and Book-wide policy changes—crosses one concrete write-side module named `BookMutations`.

`BookMutations` owns per-project write serialization, the database transaction, commit, tracking-session eviction, an in-process monotonic revision, receipt creation and best-effort publication. Mutation implementations apply changes inside the supplied transaction and return their actual effects; they do not save or publish independently. A valid operation that changes nothing returns `NoChange`, allocates no revision and publishes no receipt.

Every commit produces one `BookMutationReceipt`. A receipt carries facts rather than entity patches or reconciliation instructions: project identity, mutation identity and revision, affected facets and domain ids, structural relationships needed to preserve expansion continuity, and explicit proofs that Folder Selection or Audio Item Selection remains safe. A missing proof clears that selection. Unknown effects degrade to a whole-project rebuild and selection clearing.

Each Blazor circuit owns one concrete scoped `BookViewProjection`, bound to one project at a time. It publishes one immutable `BookViewSnapshot` containing the overview, currently loaded hierarchy branches and expansion intent, Folder Selection, Audio Item Selection, Node Status, reviews, roster/narrator state and voice previews. Razor never observes a partially reconciled candidate or mutable projection internals.

The projection interface is:

- `OpenAsync(ProjectFolderId)` — bind or switch project and build a coherent snapshot;
- `ApplyAsync(BookViewIntent)` — apply transient view gestures such as expansion, selection, Book View Mode and playback;
- `MutateAsync(BookMutation)` — commit through `BookMutations` and await the initiating projection's reconciliation;
- `RetryRebuildAsync()` — recover a Stale Book View projection.

Committed receipts enter other open projections through bounded in-process mailboxes. Each projection serializes and coalesces reconciliation, preventing an older read from replacing newer state. Mailbox pressure collapses pending detail into a safe whole-project rebuild marker. Structure, Book-wide policy, whole-project scope and unknown effects rebuild; exact paragraph, item, audio, review, roster, Node Status and voice-preview effects may use targeted authoritative reads. Rebuild restores only the overview and expanded hierarchy branches, preserving lazy loading.

The initiating projection reconciles before success is reported. Other projections converge asynchronously and cannot make the committing request fail. Targeted reconciliation failure falls back to a rebuild. If both fail, the last coherent snapshot remains visible as a **Stale Book View projection**, further Book mutation gestures are blocked, and the outcome says that the change committed but the view could not refresh.

Expected outcomes remain distinct: `NoChange`, uncommitted validation/not-found/conflict/stale/cancellation, committed-and-coherent, and committed-but-stale. Cancellation is honoured before commit; after commit, reconciliation continues under the circuit lifetime so cancellation cannot disguise a committed mutation as uncommitted. Unexpected implementation defects still throw.

The presenter becomes a thin MudBlazor adapter over the projection interface. It renders snapshots, routes gestures and translates outcomes into user feedback; it owns no reconciliation rules and does not receive the raw `BookMutations` module. Routine external attribution and audio progress reconciles silently; external structure changes or selection clearing surface a small “Book updated elsewhere” notice.

External artifacts are staged outside the database transaction. The producing adapter owns best-effort cleanup when its Book mutation is uncommitted or unchanged; the persisted Book is never pointed at an incomplete artifact.

## Considered options

- **Caller-owned reconciliation** was rejected because it is the current low-locality design: every caller must know which patches, reseeds and reloads follow its write.
- **Mutation-handler publication** was rejected because commit and publication rules would remain spread across every implementation.
- **Always rebuild** was rejected because attribution and audio progress are frequent and precisely scoped. **Always patch** was rejected because structural and denominator invariants are too broad to reproduce safely in each path.
- **A durable journal or persisted Book revision** was rejected for the current single-process application. Notifications are live invalidations for open projections; a fresh open rebuilds from the authoritative database after process restart.
- **One singleton projection for all circuits** was rejected because projection state and failure belong to one circuit, while mutation execution serves all callers.
- **A generic, extensible mutation protocol** and a projection-bound delegate were considered. The chosen interface keeps the common path small without generics or unusual caller syntax; receipts retain the useful affected-scope and safety-proof ideas.

## Consequences

- Migration is staged: route every producer through `BookMutations`; introduce the scoped projection; switch the presenter and Razor adapter; then remove `ParagraphItemsChanged`, `AudioFileAssigned` reconciliation, mutable projection state and obsolete orchestration tests.
- The generic command endpoint keeps its existing request and response contract; its adapter maps new outcomes internally.
- Tests move to the two deep module interfaces. Mutation cases assert actual-effects receipts; projection cases cover initiating and remote reconciliation, coalescing, stale-read rejection, selection safety, rebuild fallback, stale recovery and lazy branch restoration. Focused Razor interaction tests remain.
- Architecture tests require every Book mutation implementation to be registered, prevent the UI adapter from receiving raw `BookMutations`, and reject legacy reconciliation subscribers after cutover.
- Commits serialize per project but different projects remain independent. If deployment later becomes multi-process, durable revisions and publication become a new decision rather than an accidental promise of this interface.
