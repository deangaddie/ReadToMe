# Project History: snapshots plus per-step SQLite patchsets in a separate history.db

## Status

accepted (2026-10-19). Decided in the project-history map (`.scratch/project-history/`, decision ticket 05). It builds on
research tickets 02, storage options (branch `research/history-storage-options`), and 01, Onshape's document
history model (branch `research/onshape-history-model`). It respects [ADR 0007](0007-book-mutations-reconcile-book-view-projections.md):
Book mutation receipts stay in-memory and non-durable. History is a separate store with a separate purpose, not the
outbox that ADR rejected.

On 2026-10-25 the name of the measured book, which is not on Project Gutenberg, was replaced with a neutral label.
Every number is unchanged.

## Context

Users get **History** ([context/history.md](../../context/history.md)): a linear list of **History steps**, one per user
gesture. Any step can be viewed read-only as the whole project, and a step is **Restored** by adding a new step. History
is kept forever, and branches must be able to come later without a rebuild. A step fixes the **Project state**:
- the state tables of `project.db`;
- voice reference audio, the cover and the source book file.

Generated item audio, audio reviews and Pipeline checkpoints are not state.

Each project is one SQLite `project.db` (EF Core, rollback journal, migrated on open). Every state write commits
through `BookMutations.CommitAsync`: one transaction under a per-project lock.

Measured on a large novel project (8,414 paragraphs / 14,895 items):
- A `VACUUM INTO` copy is 6.19 MiB and takes 21 ms.
- A per-step session changeset is 2 KB for one paragraph edit and 1.6 MiB for a whole-book bulk assign.

| Option | Verdict |
|---|---|
| Full snapshot per step | Simplest, but ~2.5 GiB per 300 steps. It doesn't fit "kept forever". |
| Snapshot every N steps + per-step session patchsets | **Chosen.** ~50–60 MiB per 300 steps, and viewing costs at most N applies. |
| Temporal / versioned rows | EF Core temporal tables are SQL Server only, trigger shadow tables break EF's SQLite table rebuilds, and the approach is linear by nature. |
| Mutation journal (replay) | Replay isn't deterministic: Guids are minted in implementations, fractional keys depend on neighbours, attribution output comes from the LLM. |
| Dolt / Litestream | Dolt replaces SQLite and EF Core SQLite. Litestream is WAL disaster recovery, not addressable steps. |

## Decision

- **Layout.** History lives in the project folder:
  - `history/history.db` holds the step index and the chunks;
  - `history/snapshots/{stepId}.db`;
  - `history/blobs/{sha256}` holds the file copies;
  - `history/cache/{stepId}.db` holds throwaway materialised steps, a few at most, least recently used dropped first.
- **Step identity.** Each step has:
  - `StepId` (Guid), `ParentStepId`, an optional `RestoredFromStepId`;
  - a display sequence number, label, author and time;
  - an open/closed state.

  A **Head** table holds the tip, with one row in v1. A branch is another Head row. A merge step would later add a second
  parent; no column is reserved for it now.
- **Capture.** `CommitAsync` attaches `history.db` to the mutation's connection and opens a SQLite session (the session
  extension of the shipped e_sqlite3, called through DllImport on `SqliteConnection.Handle`) over the **state tables
  only**.
  - The commit's **patchset** is appended as one chunk row (step, sequence, patchset, file changes) to the open step,
    in the same transaction. Commits across attached files are atomic in rollback-journal mode.
  - Capture is at the SQLite level, so `ExecuteUpdate`/`ExecuteDelete` and checkpoint staling inside the commit are
    seen.
  - Results-only mutations commit with no session.
  - Patchsets rather than changesets, because skipped result commits leave old values (e.g. `AudioFileName`) that
    differ on replay.
- **Steps close** when their gesture finishes, or when their Background run ends or is split by a gesture. On app start,
  an open step is closed as "(interrupted)". A step with no chunks is dropped.
- **Snapshots** are `VACUUM INTO` copies, each tagged with its last migration id. One is taken:
  - when History starts (at import for a new project; "History started" for an existing one);
  - after every N closed steps (N = 50, tunable), under the write lock;
  - right after any schema migration on open.

  Chunks never cross a schema change.

  The snapshot taken when History starts is `snapshots/base.db`: the state before the first
  chunk, so a new project, whose first step is its import, has a snapshot with no step to name it
  after. A `VACUUM INTO` copy carries the project's own `__EFMigrationsHistory`, which is its
  migration tag.
- **Files.**
  - The live layout is unchanged.
  - When a state mutation writes or deletes a voice WAV (including `.orig.wav`), the cover or the source file, the new
    content is hashed into `history/blobs` before the transaction. The chunk records the path → hash change, and each
    snapshot records the full file list.
  - Blobs are deduplicated, and a leftover one is harmless.
- **Viewing.** A step is materialised into the cache as follows:
  1. Copy the nearest earlier snapshot.
  2. Apply its chunks in order.
  3. Run `MigrateAsync` on the copy.
  4. Open it with `Mode=ReadOnly`.

  If migrating the copy fails, the step is marked "can't be opened". Project readers take a state reference (the tip or
  a step id), and the agent API passes it as `?at=<stepId>`. Writes only ever go to the tip.
- **Restore** works as follows:
  1. Materialise the target.
  2. Copy the live `ParagraphItem.AudioFileName` into it for items that exist in both, and clear it for the rest.
  3. ATTACH it, and build the live → target changeset with `sqlite3session_diff` over the state tables.
  4. Apply that inside one `CommitAsync` as a **Restore** Book mutation: the discard rules of the equivalent edits apply,
     and the receipt is `WholeProject`.
  5. Once that has committed, write the files back from the blobs, as the Head's file list has them.

  The Restore is captured like any other step and carries `RestoredFromStepId`. Its file changes are the target's file
  list against the tip's, not read from the disk, so the rows are the truth and the files follow them. A file write that
  fails is logged, not a failure: the Restore has happened, and a repair at app start makes every project's state files
  as its Head lists them and clears what a crashed Restore left in `history/cache/`.

## Consequences

- Every Project-state write must go through `BookMutations`. `ProjectService` metadata and cover writes, and the voice
  audio editor's apply and restore, move onto the seam. A write that bypasses it is a History bug: it would be missing
  from every later step's patchsets until the next snapshot.
- The app depends on native SQLite session symbols that `SQLitePCLRaw` doesn't bind. A SQLite bundle upgrade must keep
  `SQLITE_ENABLE_SESSION` and `SQLITE_ENABLE_PREUPDATE_HOOK`, and a test should guard it.
- Disk use grows with re-recorded voices more than with rows. Pruning and compaction are deferred; History is kept
  forever in v1.
- The step/parent/Head shape means branching later needs no change to storage: a branch is another Head, and
  materialising a step walks parents to the nearest snapshot unchanged.
