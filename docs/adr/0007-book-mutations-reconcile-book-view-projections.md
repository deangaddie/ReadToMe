# Book mutations reconcile immutable Book View projections

## Status

accepted

## Context

The Book View is the app's only editing surface for a Book, and every producer that changes a Book
reaches it differently. `BookHierarchyPresenter` (678 lines, ~40 public members) holds mutable
copies of the hierarchy, the roster, both selections, Node Status inputs, voice previews, the view
mode and playback state — and, next to them, the follow-up rules for each gesture:
`NoteItemTextEditedAsync`, `InvalidateVoicePreview`, `ResetAndLoadAsync`, per-gesture reseeds of the
selection denominators. Each caller knows a different subset of that follow-up work. Getting a new
gesture right means rediscovering the full list, and a fix applied in one caller protects no other.

Three structural facts make that fragile rather than merely verbose.

**There is no single commit point.** `BookCommandHandler` dispatches to `ICommandHandler<T>`
implementations that each save for themselves — `SaveChangesAsync` appears 13× in `VoiceHandlers`,
7× in `CharacterHandlers`, 6× in `TitlePauseHandlers`. One user operation that touches several
records can therefore half-commit, and no caller can name the state the Book ended in. The handler's
own `finally` block evicts the tracking session with a comment explaining a stale-read bug that
eviction only papers over.

**Reconciliation is a broadcast of guesses.** `ParagraphItemsChanged` and `AudioFileAssigned` are
published by some writers and consumed in ten files. They say *something under here moved*; each
subscriber decides for itself what to reread. A subscriber that guesses too narrowly renders state
assembled from two different revisions of the Book — a count from before the change beside content
from after it.

**Not every producer even passes through the seam.** The Character Queue, the Audio Queue, imports
and rereads, AI book edits and the voice pipeline write directly. So a change made in one circuit,
or by a background worker, or through the generic command endpoint, can leave another open Book
View silently stale until the user navigates away and back.

## Decision

The persisted Book is authoritative. A Book View holds a **projection** of it, which is always
rebuildable and never a second source of truth.

Two concrete deep modules carry that rule.

**`BookMutations`** is the single write-side entry point: `CommitAsync(BookMutation)`. It owns
per-project write serialization, the database transaction, the one commit point, tracking-session
eviction, monotonic in-process revision allocation, receipt creation, and best-effort publication
*after* commit. Mutation implementations apply inside the supplied transaction and return the
effects they actually applied; they neither save nor publish on their own.

**`BookViewProjection`** is circuit-scoped and owns the read side: `OpenAsync`, `ApplyAsync`
(transient intent), `MutateAsync`, `RetryRebuildAsync`. It consumes receipts, performs its own
authoritative reads, and atomically publishes one immutable `BookViewSnapshot`. Candidate state
stays private until every read and derived calculation succeeds, so Razor can never render a
half-reconciled mixture.

A committed mutation returns a **receipt** stating project and mutation identity, the revision, any
created identity, the facets and domain identifiers actually affected, and structural split/merge
relationships. Receipts carry **facts, not instructions**: the projection decides what to reread.
Exact item-level effects permit targeted reads; structural, Book-wide and unknown effects rebuild
the overview and the expanded lazy branches. Unknown effects are safe by default — whole-project
scope.

The initiating projection reconciles before its gesture reports success. Other open projections
converge asynchronously through a bounded, coalescing in-process mailbox, which can never make the
writer's commit fail.

### Selection safety is recomputed, not proven by the writer

A mutation can invalidate a Folder Selection or an Audio Item Selection — by deleting a selected
Paragraph, by changing a roll-up denominator, or by making a selected item ineligible. The
projection **recomputes** both selections against the new revision during reconciliation, using the
same authoritative eligibility and count reads it already performs, and clears what no longer holds.
Mutation implementations report effects only.

### Revisions are process-local

Revisions are monotonic per project and live in memory. They are not persisted, not an
optimistic-concurrency column, and require no schema change. They exist to order reads against
snapshots so an older read cannot replace a newer one.

## Considered options

- **Deepen `BookCommandHandler` in place (rejected, and the closest call).** The generic command
  seam already exists, already dispatches by command type, and already owns tracking-session
  eviction; transaction ownership, serialization and receipts could have been added to it. Rejected
  because its contract — `Task<Guid?> ExecuteAsync(BookCommand, CancellationToken)` — is the whole
  problem in miniature: a nullable Guid cannot express `NoChange`, cannot distinguish a committed
  change from an expected validation failure, and carries no effects for a reader to reconcile from.
  Widening it means changing every handler signature and every call site anyway, and doing that
  *while* the old contract is still live is what a separate module makes safe. `BookMutations`
  therefore subsumes the command seam rather than sitting beside it permanently: the registry and
  the `ICommandHandler<T>` implementations become mutation implementations, and the legacy façade is
  deleted once no caller remains.
- **Writer-supplied selection-safety proofs (rejected).** Each mutation would return proof that the
  selected Paragraphs and their denominators survived. Rejected because it pushes read-side
  selection semantics into a dozen write-side handler families — the exact coupling this decision
  removes — and because "no proof ⇒ clear the selection" makes omission the cheapest option for
  every handler author, which degrades to losing the user's selection on every mutation.
- **Durable receipts / outbox (rejected for now).** Correct for multi-process deployment, and
  unnecessary at one process: a newly opened projection rebuilds from persisted data, so restart
  needs no journal. Revisit only if deployment changes.
- **Leave reconciliation in the presenter, fix bugs as found (rejected).** This is the status quo.
  Each fix protects one caller.

## Consequences

- **Every mutation producer migrates**: Book View gestures, the generic command endpoint, Character
  Queue attribution, Audio Queue result recording, imports and rereads, AI book edits, Character and
  Alias lifecycle, narrator changes, Voice and Voice Rule lifecycle, and Book-wide policy changes.
  Until the last one lands, two consistency models run side by side — which is why migration is
  sliced per producer family, each slice retiring its own legacy path rather than deferring all
  cleanup to the end.
- **Expected outcomes become a taxonomy, not exceptions**: `NoChange`; uncommitted (validation,
  not-found, conflict, stale projection, pre-commit cancellation); committed-and-coherent; and
  committed-but-stale. Unexpected implementation defects still throw. A valid operation that changes
  nothing consumes no revision, publishes no receipt, and produces no user-visible refresh.
- **Cancellation cannot lie.** It is observed before commit. Once commit begins, publication and the
  initiating reconciliation run under their owning lifetimes, so a committed change is never
  reported as uncommitted.
- **A user gesture can now queue behind a background write** for the same project, because writes
  serialize per project (different projects stay concurrent) and the initiating gesture waits for
  its own reconciliation. This is a deliberate latency cost paid for coherence; it is bounded and
  measured rather than assumed.
- **Reconciliation failure degrades, it does not blank the page.** A failed targeted refresh retries
  as a rebuild; if that also fails the last coherent snapshot stays visible, health becomes stale,
  further mutation gestures are refused, and `RetryRebuildAsync` is the recovery path.
- **`ParagraphItemsChanged` and `AudioFileAssigned` are retired** as persisted-state reconciliation
  once every producer and the presenter have moved. Queue status, Audio Gen Stream and attribution
  progress events stay — they describe live work, not persisted state.
- **The MudBlazor presenter becomes a thin adapter**: it renders snapshots, submits intents and
  mutations, and maps typed outcomes to dialogs, snackbars and the "Book updated elsewhere" notice.
  It never receives raw `BookMutations` and owns no refresh, patch, reseed or selection rule.
  Architecture tests enforce both halves of that.
- **Not changed, deliberately**: the generic command endpoint's public JSON contract; Book hierarchy,
  attribution, Voice Rule, audio-generation and assembly domain behaviour; lazy branch loading; and
  the one-process deployment assumption.
