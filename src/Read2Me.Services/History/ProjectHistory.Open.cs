using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data;
using SQLitePCL;

namespace Read2Me.Services.History;

public sealed partial class ProjectHistory
{
    /// <summary>How many materialised steps the cache keeps, most recently opened first.</summary>
    internal const int CachedSteps = 4;

    /// <summary>The last migration this build knows: the schema every materialised step is migrated to.</summary>
    private static readonly Lazy<string> LiveSchema = new(() =>
    {
        using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlite("Data Source=:memory:").Options);
        return db.Database.GetMigrations().Last();
    });

    /// <summary>One step's row, or null when the project's History has no such step.</summary>
    public async Task<HistoryStep?> GetStepAsync(ProjectFolderId folder, Guid stepId, CancellationToken ct = default)
    {
        var file = HistoryFileOf(folder);
        if (!File.Exists(file))
            return null;

        await using var connection = HistoryDatabase.Open(file, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        return await HistoryDatabase.ReadStepAsync(connection, stepId, ct);
    }

    /// <summary>
    /// <b>Opens a step</b>: the Project state after it, materialised into <c>history/cache/</c> from
    /// the nearest earlier snapshot along its parents and the chunks of every step after it,
    /// migrated to the live schema, and opened read-only. Never touches <c>project.db</c>, so the
    /// live project stays writable while a step is open.
    /// <para>
    /// A cached copy is reused while the step holds the same chunks; a step still open materialises
    /// again once it has grown. The project's results and progress (audio reviews, Pipeline
    /// checkpoints) are not History, so the copy has none. A step whose chunks fail to replay, or
    /// whose copy fails to migrate, is marked can't-be-opened, for good, and announced so open
    /// History sheets say so. A failure that passes (an IO error, a full disk, a busy database)
    /// marks nothing: opening the step later may work.
    /// </para>
    /// </summary>
    public async Task<StepOpening> OpenStepAsync(ProjectFolderId folder, Guid stepId, CancellationToken ct = default)
    {
        var file = HistoryFileOf(folder);
        var historyFolder = Path.GetDirectoryName(file)!;
        if (!File.Exists(file))
            return new StepOpening.UnknownStep();

        HistoryStep? step;
        string snapshot;
        List<(int Depth, int Sequence, byte[] Patchset)> chunks;
        await using (var connection = HistoryDatabase.Open(file, SqliteOpenMode.ReadOnly))
        {
            await connection.OpenAsync(ct);
            // One read transaction, so a commit landing meanwhile cannot tear the line from its chunks.
            await using var read = await connection.BeginTransactionAsync(ct);
            step = await HistoryDatabase.ReadStepAsync(connection, stepId, ct);
            if (step is null)
                return new StepOpening.UnknownStep();
            if (step.CantOpen)
                return new StepOpening.CantOpen(step);
            var line = await HistoryDatabase.ReadLineAsync(connection, stepId, ct);
            (var depth, snapshot) = NearestSnapshot(historyFolder, line);
            chunks = await HistoryDatabase.ReadChunksAsync(connection, stepId, depth, ct);
        }

        // Keyed by the live schema too, so a copy an older build cached is never read as current.
        var cache = HistoryDatabase.CacheIn(historyFolder);
        var cached = Path.Combine(cache, $"{stepId:N}.{chunks.Count(c => c.Depth == 0)}.{LiveSchema.Value}.db");
        if (!File.Exists(cached))
            switch (await MaterialiseAsync(snapshot, chunks, cached, ct))
            {
                case Materialised.CantOpen:
                    return new StepOpening.CantOpen(await MarkCantOpenAsync(folder, file, step, ct));
                case Materialised.TryAgain:
                    return new StepOpening.TryAgain();
            }

        File.SetLastWriteTimeUtc(cached, clock.GetUtcNow().UtcDateTime);
        PruneCache(cache, keep: cached, before: clock.GetUtcNow().UtcDateTime - CacheGrace);
        return new StepOpening.Opened(new StepState(step, cached));
    }

    /// <summary>Flags the step can't-be-opened in history.db and announces its row as it now stands.</summary>
    private async Task<HistoryStep> MarkCantOpenAsync(ProjectFolderId folder, string file, HistoryStep step, CancellationToken ct)
    {
        await using var connection = HistoryDatabase.Open(file, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(ct);
        var marked = await HistoryDatabase.MarkCantOpenAsync(connection, step.StepId, ct) ?? step with { CantOpen = true };
        events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Changed, marked));
        return marked;
    }

    /// <summary>
    /// How long a copy stays safe from pruning after it was last opened: a request opens its
    /// context after <see cref="OpenStepAsync"/> answers, and only an open file is locked.
    /// </summary>
    private static readonly TimeSpan CacheGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Copies <paramref name="snapshot"/> to a temporary file, replays the chunks root first,
    /// migrates the copy to the live schema, empties the tables History never records, and moves
    /// the copy into place — so a cached file is always a whole step, and two requests
    /// materialising the same step at once both end up with one. Leaves nothing behind when it
    /// fails: <see cref="Materialised.CantOpen"/> when a chunk fails to replay (damaged, or not
    /// matching its snapshot) or the old schema fails to migrate, which no later try changes;
    /// <see cref="Materialised.TryAgain"/> for a failure that passes (<see cref="IsTemporary"/>).
    /// </summary>
    private async Task<Materialised> MaterialiseAsync(
        string snapshot, List<(int Depth, int Sequence, byte[] Patchset)> chunks, string target, CancellationToken ct)
    {
        var pending = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(snapshot, pending);
            await using (var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={pending};Pooling=false").Options))
            {
                await db.Database.OpenConnectionAsync(ct);
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                if (CopyOpened is { } opened)
                    await opened(connection, ct);
                // The chunks recorded every cascaded delete themselves; with foreign keys on, the
                // replayed parent delete would remove those rows first and the child deletes conflict.
                await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF", ct);
                await ExecuteAsync(connection, "BEGIN", ct);
                // A chunk that changed only files has an empty patchset: nothing to replay.
                foreach (var chunk in chunks.Where(c => c.Patchset.Length > 0).OrderByDescending(c => c.Depth).ThenBy(c => c.Sequence))
                {
                    try
                    {
                        SqliteSession.Apply(connection, chunk.Patchset);
                    }
                    catch (SqliteException ex) when (!IsTemporary(ex))
                    {
                        logger.LogWarning(ex, "A History chunk failed to replay onto {Snapshot}; the step can't be opened.", snapshot);
                        return Materialised.CantOpen;
                    }
                }
                await ExecuteAsync(connection, "COMMIT", ct);

                // The chunks are at the snapshot's schema (none spans a migration), so the copy is
                // a whole step at that schema before it is brought up to the live one.
                try
                {
                    await db.Database.MigrateAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !IsTemporary(ex))
                {
                    logger.LogWarning(ex, "A History step copied from {Snapshot} failed to migrate; it can't be opened.", snapshot);
                    return Materialised.CantOpen;
                }

                foreach (var table in NotStateTables(db))
                    await ExecuteAsync(connection, $"DELETE FROM \"{table}\"", ct);
                // A read-only open of a WAL database needs its -shm file; a rollback journal needs nothing.
                await ExecuteAsync(connection, "PRAGMA journal_mode = DELETE", ct);
            }
            try
            {
                File.Move(pending, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another request materialised the same step first; its copy is the same state.
            }
            return Materialised.Done;
        }
        catch (Exception ex) when (IsTemporary(ex))
        {
            logger.LogWarning(ex, "A History step copied from {Snapshot} failed to open for now; it can be tried again.", snapshot);
            return Materialised.TryAgain;
        }
        finally
        {
            // A leftover copy is only clutter; it must not turn the answer into an error.
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Keeps the <see cref="CachedSteps"/> most recently opened steps, and any opened since
    /// <paramref name="before"/>, which a request may be about to read. A copy another request
    /// still reads is locked on Windows and stays until a later prune.
    /// </summary>
    private static void PruneCache(string cache, string keep, DateTime before)
    {
        var stale = new DirectoryInfo(cache).GetFiles("*.db")
            .Where(f => !string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(CachedSteps - 1)
            .Where(f => f.LastWriteTimeUtc < before);
        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Tests only: runs on a copy's connection once it opens, to play a full disk or a busy database.</summary>
    internal Func<SqliteConnection, CancellationToken, Task>? CopyOpened { get; set; }

    private enum Materialised { Done, CantOpen, TryAgain }

    /// <summary>
    /// A failure that says nothing about the step: an IO error, or SQLite reporting an IO error, a
    /// full disk, or a busy or locked database. A missing file is not one: it won't come back.
    /// </summary>
    private static bool IsTemporary(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => false,
        IOException => true,
        SqliteException { SqliteErrorCode: raw.SQLITE_BUSY or raw.SQLITE_LOCKED or raw.SQLITE_IOERR or raw.SQLITE_FULL } => true,
        { InnerException: { } inner } => IsTemporary(inner),
        _ => false,
    };

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
