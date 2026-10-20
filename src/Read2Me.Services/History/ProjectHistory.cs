using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// What a request commits is one step, through its <see cref="HistoryStepScope"/>; a state
/// mutation committed with no scope open is a step of its own, labelled from the mutation.
/// Results-only mutations, and state mutations that change no state table, record nothing.
/// </para>
/// </summary>
public sealed partial class ProjectHistory(
    IFileSystem fs,
    IProjectDbContextFactory dbFactory,
    ProjectWriteLocks writeLocks,
    IOptions<BookMutationOptions> mutationOptions,
    EventBroadcaster<HistoryStepEvent> events,
    TimeProvider clock,
    ILogger<ProjectHistory> logger)
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

    /// <summary>
    /// Closes a step scope's current step, if a commit created one, and announces it.
    /// <paramref name="ending"/> is how a Background run ended early ("212 of 340 paragraphs,
    /// cancelled"); it goes in the note even when another commit already closed the step. Takes
    /// the project's write lock, so the close never races a commit extending the line, but waits
    /// only the mutation budget: a step left open is closed by the next commit to the project
    /// instead.
    /// </summary>
    internal async Task CloseAsync(HistoryStepScope.OpenScope scope, string? ending = null)
    {
        var folder = scope.Folder;
        // Not the request's token: a request that ends, however it ends, still closes its step.
        using var writeLock = await writeLocks.AcquireAsync(folder, mutationOptions.Value.LockWaitBudget, CancellationToken.None);
        if (writeLock is null)
        {
            logger.LogWarning(
                "History step {Step} on {Folder} stays open, without its ending {Ending}: the write lock was busy past {Budget}. The next commit closes it.",
                scope.StepId, folder.Value, ending, mutationOptions.Value.LockWaitBudget);
            return;
        }
        // Read under the lock: a commit holding it may still be moving the scope onto a new step.
        if (scope.StepId is not { } stepId)
            return;

        var note = ending is null ? null : StepNotes.Of(scope.Part, ending);
        HistoryStep? closed;
        await using (var connection = HistoryDatabase.Open(HistoryFileOf(folder), SqliteOpenMode.ReadWrite))
        {
            await connection.OpenAsync();
            closed = await HistoryDatabase.CloseStepAsync(
                connection, null, schema: "", stepId, clock.GetUtcNow(), note, CancellationToken.None);
        }
        if (closed is not null)
            events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Closed, closed));
    }

    /// <summary>
    /// Closes every step <paramref name="folder"/>'s History left open as "interrupted": what app
    /// start does, when nothing that opened a step is running any more. A project with no History
    /// yet has nothing to close.
    /// </summary>
    public async Task CloseInterruptedAsync(ProjectFolderId folder, CancellationToken ct = default)
    {
        var file = HistoryFileOf(folder);
        if (!File.Exists(file))
            return;

        using var writeLock = await writeLocks.AcquireAsync(folder, mutationOptions.Value.LockWaitBudget, ct);
        if (writeLock is null)
            return; // something is already writing, so History is in use: its own commits close what was left open

        IReadOnlyList<HistoryStep> closed;
        await using (var connection = HistoryDatabase.Open(file, SqliteOpenMode.ReadWrite))
        {
            await connection.OpenAsync(ct);
            closed = await HistoryDatabase.CloseInterruptedAsync(connection, ct);
        }
        foreach (var step in closed)
            events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Closed, step));
    }

    /// <summary>Announces what a commit did to History. Call after the commit, never inside it.</summary>
    internal void Announce(ProjectFolderId folder, RecordedStep recorded)
    {
        foreach (var closed in recorded.Closed)
            events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Closed, closed));
        if (recorded.Changed is { } changed)
            events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Changed, changed));
        if (recorded.Landed is not { } step)
            return;
        events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Landed, step));
        if (!step.IsOpen)
            events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Closed, step));
    }

    /// <summary>"SetItemSpeakerMutation" → "Set item speaker": the label of a step no scope names.</summary>
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
        var step = new HistoryStep(Guid.NewGuid(), null, 1, HistoryDatabase.StartedLabel, null, Author, now, now, IsOpen: false, Note: null);
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
