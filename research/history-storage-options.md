# Storage options for History in SQLite / EF Core

Research for wayfinder ticket `.scratch/project-history/issues/02-history-storage-options.md`
(map `.scratch/project-history/map.md`, glossary `context/history.md`). Researched 2026-10-19.
This file states facts and trade-offs; the storage **decision** belongs to a later grilling ticket.

Terms follow `context/history.md`: **History**, **History step**, **Project state**, **View a step**, **Restore**.

## 1. What the codebase constrains

- One `project.db` per project, opened by `ProjectDbContextProvider.CreateAsync` with
  `Data Source={dbPath};Pooling=false`, then `Database.MigrateAsync()` on every open
  (`src/Read2Me.Services/ProjectDbContextFactory.cs`). Default rollback journal: no `journal_mode`/WAL
  setting anywhere in `src/`.
- 16 EF migrations so far (`src/Read2Me.Data/Migrations`), the latest `20261017120721_AddStepCheckpoints`.
  The schema changes regularly, so **every option must read state written under an older schema**.
- All rows have a `Guid Id` primary key (stored as TEXT by EF Core SQLite) and fractional `Order` strings
  (`src/Read2Me.Data/datamodel.md`). Every table has a declared PRIMARY KEY, which is what the SQLite
  session extension requires (section 3).
- The write seam is `BookMutations.CommitAsync`: per-project write lock, one transaction, `SaveChangesAsync`,
  commit, then a receipt with a monotonic revision (`src/Read2Me.Services/Mutations/BookMutations.cs`).
  Implementations also write with `ExecuteUpdateAsync`/`ExecuteDeleteAsync`
  (`CharacterLifecycleMutationImplementations.cs` and others), and `CommitAsync` itself runs an
  `ExecuteUpdateAsync` to stale checkpoints. These bypass EF's change tracker, so **any capture that hooks
  the change tracker (SaveChanges interceptors, `ChangeTracker` entries) misses them**. Capture must happen
  at the SQLite level, or the History step must be taken from the database after commit.
- Writes that bypass the seam: `ProjectService` (metadata and cover) calls `SaveChangesAsync` on its own
  context. TTS service configurations live in the **global `app.db`** (`ParagraphTtsSettingsService`,
  "CRUD + active-selection for paragraph-TTS service configurations", uses `Read2MeDbContext`). Any setting
  that shapes output and sits there is outside every `project.db`-level mechanism below; the History spec must
  say whether such settings count as Project state.
- A History step is one user action, but the attribution queue commits once per paragraph. Every option
  needs a "step boundary" concept that groups several commits into one step.
- `project.db` also holds results that are not Project state: `ParagraphItem.AudioFileName`, `AudioReviews`,
  `StepCheckpoints`. Restore must leave these alone or reconcile them (stale takes), whatever the storage.

## 2. Local measurements (scratch copies of real workspace projects, never written in place)

Copies of `D:\Dev\Read\WS\dune\project.db` and `pride-and-prejudice\project.db`, measured with `bun:sqlite`
(SQLite 3.53.2) and a .NET 10 file-based app on `Microsoft.Data.Sqlite` 10.0.13 + `SQLitePCLRaw.bundle_e_sqlite3`
3.0.5 (the repo's pinned versions).

| Project | Paragraphs | Items | `project.db` | `VACUUM INTO` copy | copy, gzip | `VACUUM INTO` time |
|---|---|---|---|---|---|---|
| Dune | 8,414 | 14,895 | 6.75 MiB | 6.19 MiB | 2.49 MiB | 21 ms |
| Pride and Prejudice | 4,225 | 5,297 | 3.02 MiB | 2.80 MiB | 1.09 MiB | 12 ms |

- Dune's space by object (`dbstat`): `ParagraphItems` 3.1 MiB, its three indexes 1.9 MiB, `Paragraphs` +
  two indexes 1.5 MiB. Item text is only 1.1 MiB; most bytes are 36-char TEXT Guids repeated in rows and indexes.
- Extrapolated to the ticket's typical book (~20k items): **~8–9 MiB per full snapshot, ~3.3 MiB gzipped**.
  300 steps of full snapshots: **~2.5 GiB raw, ~1 GiB gzipped**.
- Session-extension changesets (section 3) for realistic mutations on Dune:

| Mutation | Rows changed | Changeset | Inverse | gzip |
|---|---|---|---|---|
| Edit one paragraph's text | 8 | 1,987 B | 1,987 B | 1,229 B |
| Attribute one chapter (CharacterId + delivery cue) | 70 | 8,978 B | 8,978 B | 1,671 B |
| Rename all 61 chapter titles | 61 | 4,041 B | 4,041 B | 1,379 B |
| Bulk-assign every item in the book | 14,895 | 1.60 MiB | 1.60 MiB | 330 KiB |

- The e_sqlite3 native library the repo ships (NuGet `SQLite` 3.53.4, pulled in by
  `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5) **exports the session API** (`sqlite3session_*`, `sqlite3changeset_*`,
  including `_invert`, `_concat`, `_apply_v2`) and was built with `ENABLE_SESSION` and `ENABLE_PREUPDATE_HOOK`.
  The managed `SQLitePCLRaw.core` 3.0.5 has **no** session bindings (it does bind `backup_*`,
  `sqlite3_serialize`, `sqlite3_deserialize`). The measurement above used plain `[DllImport("e_sqlite3")]`
  against `SqliteConnection.Handle.DangerousGetHandle()`; it worked first time, including over a statement
  issued like `ExecuteUpdate` (a set-based UPDATE).
- Voice reference audio: the Alice workspace has 61 voice WAVs, ~31.6 MB with transcripts, about 0.5 MB per
  voice. Covers are ~20 KB. Files dominate size whenever voices change, so they must be content-addressed
  under any option.

## 3. Source facts

### SQLite

- **VACUUM INTO** writes "a vacuumed copy" to a new file and leaves the original unchanged; the output "is a
  consistent snapshot of the original database", "minimal in size", and "all deleted content is purged". The
  target file must not exist or be empty. It is not a write on the source, so it needs no write lock. An
  interrupted VACUUM INTO "might be incomplete and corrupt" (write to a temp name, then rename).
  Compared with the backup API: "the backup API uses fewer CPU cycles and can be executed incrementally."
  — <https://www.sqlite.org/lang_vacuum.html>
- **Online backup API** makes the destination "a bit-wise identical copy of the source database as it was when
  the copying commenced" (not vacuumed). Writes from another connection mid-copy make it restart.
  — <https://www.sqlite.org/backup.html>. `sqlite3_backup_init` fails "if there is already a read or read-write
  transaction open on the destination database", and the destination connection must not be used during the
  backup — <https://www.sqlite.org/c3ref/backup_finish.html>. Restoring by backing a snapshot *onto* the live
  `project.db` therefore needs every other connection closed and bypasses `CommitAsync`'s transaction.
- **Session extension** records, per table with a declared PRIMARY KEY, the PK of inserted rows and the PK plus
  original values of updated/deleted rows, coalescing repeat changes; virtual tables and NULL-PK rows are not
  captured. A *patchset* drops old values (smaller, weaker conflict detection). `sqlite3changeset_invert()`
  yields the undo changeset. Needs `SQLITE_ENABLE_SESSION` + `SQLITE_ENABLE_PREUPDATE_HOOK` (present in the
  shipped e_sqlite3, section 2). — <https://www.sqlite.org/sessionintro.html>
- **Changeset apply** treats a target table as compatible if it has the same name, **at least as many columns**,
  and the PK **in the same position**; an incompatible table is skipped with only a `SQLITE_SCHEMA` log warning,
  not an error. Columns are matched **by position, not name**; extra trailing target columns get defaults on
  INSERT. — <https://www.sqlite.org/session/sqlite3changeset_apply.html>
- **`sqlite3session_diff`** builds the changeset that turns a table in an attached database into the session's
  table, provided both have "the same set of columns declared in the same order" and the same PK.
  — <https://www.sqlite.org/session/sqlite3session_diff.html>. This is a ready-made Restore primitive:
  `ATTACH` the (migrated) step snapshot and diff it against `main` inside one transaction.
- **`sqlite3_serialize`** returns an in-memory serialization of a database and is available unless built with
  `SQLITE_OMIT_DESERIALIZE`. — <https://www.sqlite.org/c3ref/serialize.html>

### EF Core and Microsoft.Data.Sqlite

- **Temporal tables are SQL Server only.** The EF Core page lives under the SQL Server provider and is titled
  "Microsoft SQL Server Database Provider - Temporal Tables"; `IsTemporal()`, `TemporalAsOf`, `TemporalAll` etc.
  rely on SQL Server's `SYSTEM_VERSIONING`. — <https://learn.microsoft.com/en-us/ef/core/providers/sql-server/temporal-tables>
  The SQLite provider's limitations page has no temporal support, and lists migrations that need a **table
  rebuild** (AlterColumn, DropColumn, AddForeignKey, DropForeignKey, AddPrimaryKey, …); objects not in the EF
  model, such as hand-written triggers, make a rebuild throw `NotSupportedException`.
  — <https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations>
- **Microsoft.Data.Sqlite** supports `Mode=ReadOnly` and URI filenames in `Data Source`
  — <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings>; and
  `SqliteConnection.BackupDatabase`, which "will back up the database as quickly as possible and blocks other
  connections from writing" — <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup>.
  It has no session-extension API (P/Invoke needed, section 2).
- Consequence for viewing: a snapshot can be opened read-only by a `ProjectDbContext` **only if its schema is
  current**. The current factory always calls `MigrateAsync`, which writes. An old-schema snapshot must be
  copied to a temp file and migrated there (EF reads `__EFMigrationsHistory` from the file), then opened with
  `Mode=ReadOnly`.

### Dolt

- "A SQL database you can fork, clone, branch, merge, push and pull just like a Git repository"; you connect
  "with any MySQL client". — <https://www.dolthub.com/docs/introduction/what-is-dolt>
- `AS OF` accepts a commit hash, branch or timestamp, per table; `dolt_history_<table>` gives "a row for every
  revision of a row"; `SHOW CREATE TABLE … AS OF` reads past schemas.
  — <https://www.dolthub.com/docs/sql-reference/version-control/querying-history>
- Storage is prolly trees over content-addressed chunks that "structurally share portions of the tree between
  versions", and diff cost "scales with the size of the differences".
  — <https://www.dolthub.com/docs/architecture/storage-engine/prolly-tree>
- The embedded driver is Go only (`github.com/dolthub/driver`); from .NET it means running `dolt sql-server`
  and a MySQL EF Core provider. That replaces SQLite per project.

### Litestream

- A separate background process that ships WAL pages to a replica; v0.5 packages them as LTX files by TXID,
  compacts levels, snapshots every 24 h, and keeps 24 h by default. Restore points are file boundaries, "not
  arbitrary transactions", and coarsen as retention prunes. — <https://litestream.io/how-it-works/>
- It requires WAL mode (the repo uses the rollback journal), restores whole databases only, and has no
  notion of a user-labelled step. It is disaster recovery, not an addressable History.

## 4. The options

### (a) Full snapshot per step (`VACUUM INTO` / backup API)

After the step's last commit, `VACUUM INTO 'history/{stepId}.db.tmp'` and rename. ~20 ms for Dune, consistent
without the write lock. Size is the problem: ~8–9 MiB per step for a 20k-item book, ~2.5 GiB over 300 steps
(~1 GiB gzipped, but gzipped files can't be opened in place). **View**: open the file (migrate a temp copy
first if its schema is old). **Restore**: `ATTACH` the snapshot and apply `sqlite3session_diff` (or
delete-and-copy table by table) in one `CommitAsync` transaction, leaving result tables alone. **Migrations**:
easy and robust, because each snapshot carries its own `__EFMigrationsHistory` and EF migrates a copy.
**Bypass writes**: harmless, the snapshot reads the file, whoever wrote it. **Branches**: trivial, a step is a
file plus a parent pointer. Verdict: simplest and most robust, ruled out on size alone for "kept forever".

### (b) Snapshot every N steps plus a log in between

Snapshot (as in a) every N steps **and at every schema migration**; between them, store each step's session
changeset (several commits combined with `sqlite3changeset_concat`), plus its inverse if backward walks are
wanted. Measured log entries are 1–9 KB for ordinary steps and ~1.6 MiB (330 KiB gzipped) for a whole-book
bulk assign. With N = 50: 300 steps ≈ 6 snapshots (~50 MiB) + logs (a few MiB). **View**: copy the nearest
earlier snapshot to temp, apply up to N changesets (all written under that snapshot's schema), then migrate,
then open read-only. Tens of ms to about a second. **Restore**: as in (a), diff the materialised view against
`main`. **Migrations**: safe *if* a snapshot is forced before any migration runs. That is the only rule that
keeps positional changeset columns (section 3) from silently misapplying after a rebuild. **Capture**: a
session object attached to the connection `CommitAsync` uses records `SaveChanges` *and* `ExecuteUpdate`/
`ExecuteDelete` alike. It does **not** see writes on other connections (`ProjectService`), so those need routing
through the seam or a step snapshot. A safety net: if a step's changeset can't be trusted, take a snapshot.
**Branches**: a step has a parent; to view a step, walk parents back to the nearest snapshot. Works
unchanged with several heads. Verdict: best size/robustness balance; needs P/Invoke for the session API.

### (c) Temporal / versioned rows (valid-from/valid-to step columns, or trigger-kept shadow tables)

EF Core gives no help on SQLite (temporal is SQL Server only). Valid-from/to columns on every table change
every query and every index in the app, including unique constraints such as one `AudioReview` per item, and
every `ExecuteUpdate` becomes "close old row, insert new". Shadow tables kept by triggers capture all writers,
whichever connection, including bulk writes. But triggers are not in the EF model, so EF's table rebuilds throw
`NotSupportedException` or need hand-written migrations that drop and re-create them (EF limitations page).
Each migration must also be mirrored into every shadow table. Size is good: only changed rows, about
changeset size plus Guid overhead. **View**: an `AS OF step` query per table, so it cannot reuse
`ProjectDbContext` without global query filters, or must be materialised into a temp db first.
**Branches**: valid-from/to is linear; branches need per-row step sets or a parent walk, which becomes
version-vector logic. Verdict: most invasive, worst migration story, poor branch fit. Not recommended.

### (d) Mutation journal (replay forward, or stored inverse operations)

Persist each `BookMutation` record (domain operation and arguments) and replay from the start or a snapshot.
Replay correctness depends on every implementation being deterministic forever. Many are not: Guids are minted
in the implementation, fractional keys depend on neighbours, attribution results come from the LLM, and
`ProjectService` writes are not mutations at all. Every change to a mutation's code becomes a history
migration problem, and ADR 0007 already rejected a durable receipt store "for now". Stored inverse
*operations* fail the same way. Stored inverse *row data* is just (b) with the session extension doing it
safely. Verdict: keep the mutation name and arguments as a **label** for the History list, never as the source
of state. Not recommended for storage.

### (e) Content-addressed storage (git-like rows/files; Dolt; session changesets; Litestream)

- *Rows content-addressed by hash, with a manifest per step.* Dedupe is excellent, but a flat manifest of ~23k
  row hashes is ~0.7 MiB per step unless it is tree-structured (per chapter, per table), which means building a
  small Merkle store yourself. View and Restore must materialise a database. Migrations need re-hashing or
  per-schema readers. Branch fit is ideal.
- *Files* (voice WAVs, cover): content-addressed blobs (`history/blobs/{sha256}`), referenced from the step's
  state. This is needed under **every** option, because `voices/{characterId}/…` and `cover.jpg` are
  overwritten in place today. At ~0.5 MB per voice, re-recording dominates growth, but each blob is stored
  once however many steps refer to it.
- *Dolt* gives all of this natively (structural sharing, `AS OF`, branches, schema history), but it means
  replacing SQLite/EF Core SQLite with a MySQL-protocol server process per workspace. That is a platform
  rewrite, out of proportion for v1.
- *SQLite session changesets* are the practical "git-like delta" inside SQLite. They are the log in (b).
- *Litestream/LiteFS* are WAL-shipping disaster recovery: they need WAL mode, offer time-based rather than
  step-addressed restore points, and prune by retention. Not a History store.

Verdict: content-addressed **blobs for files** belong in any design. Content-addressed **rows** and Dolt are
not worth it at this scale.

## 5. Comparison

Size: typical book of ~20k items, 300 steps.

| | Size over 300 steps | View a step | Restore | Old schemas | Bypass writes / bulk SQL | Branch fit | Build cost |
|---|---|---|---|---|---|---|---|
| (a) snapshot per step | ~2.5 GiB (≈1 GiB gz, not openable) | open file (migrate temp copy if old) | attach + `session_diff`, one txn | robust: each file migrates itself | all captured | trivial | low |
| (b) snapshot every N + changeset log | ~50–60 MiB at N=50 | temp copy + ≤N applies + migrate | same as (a) | safe if a snapshot is forced before each migration | connection-scoped; other connections need routing or a snapshot | good: parent walk to snapshot | medium (P/Invoke session API) |
| (c) temporal rows / shadow triggers | small (changed rows) | `AS OF` queries or materialise | rewrite current rows | poor: triggers block EF rebuilds; shadow schema mirrors every migration | triggers catch all; valid-to columns need every writer changed | poor (linear validity) | high, touches every query |
| (d) mutation journal | tiny | replay, non-deterministic | replay | poor: old mutation code must live forever | misses `ProjectService` | fine in principle | high, fragile |
| (e) CA rows / Dolt | small, deduped | materialise / `AS OF` (Dolt) | materialise / Dolt checkout | per-schema readers / Dolt native | Dolt: all; CA rows: needs capture | ideal | very high (Dolt = new database) |
| files as CA blobs | each distinct WAV once (~0.5 MB) | resolve by hash | copy blob back | n/a | `ProjectService` cover path must route | ideal | low, needed by all |

## 6. Leading candidate (recommendation only; the decision is for the grilling ticket)

**(b) plus content-addressed file blobs**: a per-project `history/` folder holding periodic `VACUUM INTO`
snapshots, which are also forced at every schema migration and when a step can't be captured, plus a per-step
session changeset (concat of the step's commits) and an `history.db` index of steps (id, parent id, label from
the mutation name, snapshot or changeset reference, file-blob manifest). View materialises a temp db and
opens it `Mode=ReadOnly`. Restore is a `sqlite3session_diff` against the attached materialised state, committed
as a new step through `CommitAsync`. A cheap first slice is plain (a) with a step index and parent pointer,
since the format generalises to (b) by making most steps changesets later.

Open points for the grilling:
- Where step boundaries come from for multi-commit actions (attribution run).
- Whether `ProjectService` and the `app.db` TTS settings move into `project.db` and the seam.
- How Restore treats result columns and tables (`AudioFileName`, `AudioReviews`, `StepCheckpoints`).
- Whether N is a step count or a byte budget.
