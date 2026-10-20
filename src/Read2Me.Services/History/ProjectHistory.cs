using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.History;

/// <summary>
/// A project's <b>History</b> (context/history.md, ADR 0016): a linear list of History steps kept in
/// a <c>history/</c> folder beside <c>project.db</c>, recorded in the same transaction as the change
/// each one fixes.
/// <para>
/// History starts the first time a project opens or is written: a base snapshot of the project as
/// it stands, plus a "History started" step when the project already has a Book. A project with no
/// Book yet gets no such step, so its import is its first step.
/// </para>
/// <para>
/// No step scopes yet: each committed state mutation is a step of its own, labelled from the
/// mutation. Results-only mutations, and state mutations that change no state table, record nothing.
/// </para>
/// </summary>
public sealed partial class ProjectHistory(
    IFileSystem fs,
    IProjectDbContextFactory dbFactory,
    ProjectWriteLocks writeLocks,
    IOptions<BookMutationOptions> mutationOptions,
    EventBroadcaster<HistoryStepEvent> events,
    TimeProvider clock)
{
    /// <summary>Who steps are recorded for. The app is single-user, so it is whoever runs it.</summary>
    private static readonly string Author = Environment.UserName;

    /// <summary>The steps newest first, and the Head. Starts History if this is the project's first open.</summary>
    public async Task<HistoryList> ListAsync(ProjectFolderId folder, CancellationToken ct = default)
    {
        await StartAsync(folder, ct);
        var file = HistoryFileOf(folder);
        if (!File.Exists(file))
            return new HistoryList([], null); // a long commit held the lock; it starts History itself

        await using var connection = HistoryDatabase.Open(file, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);

        var steps = new List<HistoryStep>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {HistoryDatabase.StepColumns} FROM Step ORDER BY Sequence DESC";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                steps.Add(HistoryDatabase.ReadStep(reader));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT StepId FROM Head WHERE Id = 1";
            var head = await command.ExecuteScalarAsync(ct) as string;
            return new HistoryList(steps, head is null ? null : Guid.Parse(head));
        }
    }

    /// <summary>
    /// Starts the project's History if it has none: what opening a project does. Takes the
    /// project's write lock, so the base snapshot never races a commit History would miss — but
    /// waits only the mutation budget for it. A writer that holds the lock longer is itself a
    /// commit, and starts History before it writes, so nothing is missed by giving up.
    /// </summary>
    public async Task StartAsync(ProjectFolderId folder, CancellationToken ct = default)
    {
        if (File.Exists(HistoryFileOf(folder)))
            return;

        using var writeLock = await writeLocks.AcquireAsync(folder, mutationOptions.Value.LockWaitBudget, ct);
        if (writeLock is null)
            return;
        await using var db = await dbFactory.CreateAsync(fs.GetProjectFolderPath(folder));
        await StartUnderLockAsync(db, ct);
    }

    /// <summary>
    /// Starts History if needed, then begins capturing one state mutation on its connection. The
    /// caller holds the project's write lock and has not yet begun its transaction.
    /// </summary>
    internal async Task<HistoryCapture> BeginCaptureAsync(ProjectDbContext db, CancellationToken ct)
    {
        await StartUnderLockAsync(db, ct);
        return await HistoryCapture.BeginAsync(
            db, HistoryDatabase.FileIn(HistoryFolderOf(db)), NotStateTables(db), Author, clock, ct);
    }

    /// <summary>Announces a step that landed with its commit. Call after the commit, never inside it.</summary>
    internal void Announce(ProjectFolderId folder, HistoryStep step)
    {
        events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Landed, step));
        if (!step.IsOpen)
            events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Closed, step));
    }

    /// <summary>"SetItemSpeakerMutation" → "Set item speaker", until step scopes name steps in user terms.</summary>
    internal static string LabelOf(BookMutation mutation)
    {
        var words = PascalWords().Split(mutation.Name.EndsWith("Mutation", StringComparison.Ordinal)
            ? mutation.Name[..^"Mutation".Length]
            : mutation.Name);
        return string.Join(' ', words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }

    /// <summary>
    /// The snapshot first, then history.db written under a temporary name and moved into place, so
    /// a history.db that exists always has its base snapshot beside it.
    /// </summary>
    private async Task StartUnderLockAsync(ProjectDbContext db, CancellationToken ct)
    {
        var folder = HistoryFolderOf(db);
        var file = HistoryDatabase.FileIn(folder);
        if (File.Exists(file))
            return;

        var snapshots = HistoryDatabase.SnapshotsIn(folder);
        Directory.CreateDirectory(snapshots);
        var snapshot = Path.Combine(snapshots, HistoryDatabase.BaseSnapshotName);
        File.Delete(snapshot); // a leftover from a start that never finished; VACUUM INTO will not overwrite
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [snapshot], ct);

        // A project with a Book is an existing one meeting History for the first time; one without
        // has not been imported yet, and its import will be its first step.
        var existing = await db.Volumes.AnyAsync(ct);

        var pending = file + ".tmp";
        File.Delete(pending);
        await using (var connection = HistoryDatabase.Open(pending, SqliteOpenMode.ReadWriteCreate))
        {
            await connection.OpenAsync(ct);
            HistoryDatabase.CreateSchema(connection);
            if (existing)
                await InsertStartedStepAsync(connection, ct);
        }
        File.Move(pending, file);
    }

    /// <summary>
    /// The root step of an existing project. It has no chunk: its state is the base snapshot. Step
    /// scopes that drop chunkless steps on close must leave it alone.
    /// </summary>
    private Task InsertStartedStepAsync(SqliteConnection connection, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var step = new HistoryStep(Guid.NewGuid(), null, 1, HistoryDatabase.StartedLabel, Author, now, now, IsOpen: false);
        return HistoryDatabase.AppendStepAsync(connection, null, schema: "", step, patchset: null, ct);
    }

    /// <summary>The project's tables History never records: results (AudioReviews) and progress (StepCheckpoints).</summary>
    private static string[] NotStateTables(ProjectDbContext db) =>
    [
        db.Model.FindEntityType(typeof(AudioReview))!.GetTableName()!,
        db.Model.FindEntityType(typeof(StepCheckpoint))!.GetTableName()!,
    ];

    private string HistoryFileOf(ProjectFolderId folder) =>
        HistoryDatabase.FileIn(HistoryDatabase.FolderOf(fs.GetProjectFolderPath(folder)));

    private static string HistoryFolderOf(ProjectDbContext db) =>
        HistoryDatabase.FolderOf(Path.GetDirectoryName(((SqliteConnection)db.Database.GetDbConnection()).DataSource)!);

    [GeneratedRegex("(?<!^)(?=[A-Z])")]
    private static partial Regex PascalWords();
}
