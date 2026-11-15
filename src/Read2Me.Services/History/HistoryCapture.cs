using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.History;

/// <summary>
/// Records one state commit as a History step, on the mutation's own connection (ADR 0016): the
/// one History operation a commit is, in its phases.
/// <list type="number">
/// <item>Begun (<see cref="HistoryStepScope.BeginCaptureAsync"/>) under the project's write lock,
/// before the mutation's transaction: ATTACH cannot run inside one, and the session must be
/// recording before the first write.</item>
/// <item><see cref="RecordAsync"/> inside the transaction, after <c>SaveChanges</c>, so the step and
/// the change it records commit or roll back together — the attached history.db shares the
/// transaction.</item>
/// <item>Disposed after the transaction ends, which detaches history.db.</item>
/// <item><see cref="CommittedAsync"/> once the commit stands, still under the lock: only then does
/// the step scope move. A capture whose transaction rolled back, or that threw before its commit, is
/// never told, and moves nothing.</item>
/// <item><see cref="Announce"/> after the lock is released.</item>
/// </list>
/// The last two read only what was recorded, so they run after disposal.
/// </summary>
internal sealed class HistoryCapture : IAsyncDisposable
{
    private readonly ProjectHistory _history;
    private readonly ProjectFolderId _folder;
    private readonly HistoryStepScope.OpenScope? _scope;
    private readonly BackgroundRunKind? _runKind;
    private readonly ProjectDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly SqliteSession _session;
    private readonly string _author;
    private readonly TimeProvider _clock;
    private readonly FileList _tipFiles;
    private RecordedStep? _recorded;
    private bool _committed;

    private HistoryCapture(
        ProjectHistory history, ProjectFolderId folder, HistoryStepScope.OpenScope? scope, BackgroundRunKind? runKind,
        ProjectDbContext db, SqliteConnection connection, SqliteSession session, string author, TimeProvider clock,
        FileList tipFiles)
    {
        _history = history;
        _folder = folder;
        _scope = scope;
        _runKind = runKind;
        _db = db;
        _connection = connection;
        _session = session;
        _author = author;
        _clock = clock;
        _tipFiles = tipFiles;
    }

    /// <summary>
    /// Attaches <paramref name="historyFile"/> and starts a session over every project table except
    /// <paramref name="notState"/> and SQLite's and EF's own bookkeeping. The connection is held open
    /// until disposal, because a session lives on one native handle. <paramref name="tipFiles"/> is the
    /// state files' list at the tip, which the commit's file changes are taken against. What is
    /// recorded lands in <paramref name="scope"/>, as Background run work of <paramref name="runKind"/>
    /// when it is a run, or is a step of its own when no scope holds it.
    /// </summary>
    public static async Task<HistoryCapture> BeginAsync(
        ProjectHistory history, ProjectFolderId folder, HistoryStepScope.OpenScope? scope, BackgroundRunKind? runKind,
        ProjectDbContext db, string historyFile, IReadOnlyCollection<string> notState, string author, TimeProvider clock,
        FileList tipFiles, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        SqliteSession? session = null;
        try
        {
            await using (var attach = connection.CreateCommand())
            {
                attach.CommandText = $"ATTACH DATABASE $path AS {HistoryDatabase.Schema}";
                attach.Parameters.AddWithValue("$path", historyFile);
                await attach.ExecuteNonQueryAsync(ct);
            }

            var stateTables = await StateTablesAsync(connection, notState, ct);
            session = SqliteSession.Create(connection);
            foreach (var table in stateTables)
                session.Attach(table);
            return new HistoryCapture(history, folder, scope, runKind, db, connection, session, author, clock, tipFiles);
        }
        catch
        {
            session?.Dispose();
            await DetachAndCloseAsync(db, connection);
            throw;
        }
    }

    /// <summary>
    /// Records what the session captured, inside the mutation's transaction — or nothing, when no
    /// state table and no state file changed. The scope is not moved yet: that waits for
    /// <see cref="CommittedAsync"/>.
    /// <para>
    /// Inside a step scope whose step is still the open tip, the change is that step's next chunk.
    /// Otherwise it starts a new step at the tip: open for the scope to extend and close, or closed
    /// at once when no scope is open. A step left open at the tip by anyone else — a scope that
    /// could not close it, or one this commit interleaves with — is closed first, so a step's chunks
    /// are always contiguous in the line.
    /// </para>
    /// <para>
    /// The state files changed too: those the rows stop or start naming, and those the mutation
    /// writes over in place (<see cref="ProjectStateMutation.WritesFiles"/>), each hashed into the blobs before the chunk
    /// that names it. A commit that changed only a file — an edit of a Voice's audio — is still a
    /// step. A Restore records its own (<see cref="ProjectStateMutation.RecordsFileChanges"/>).
    /// </para>
    /// </summary>
    public async Task RecordAsync(ProjectStateMutation mutation, BookMutationEffects effects, CancellationToken ct) =>
        _recorded = await RecordStepAsync(mutation, effects, ct);

    /// <summary>
    /// The commit stands: moves the step scope onto the step it landed in or extended, then takes a
    /// snapshot if one is due, and answers that step, whose label and action name the commit. Null,
    /// with no snapshot, when nothing was recorded. The caller still holds the project's write lock,
    /// so the snapshot is the state after this commit. Throws as History does; the caller decides
    /// that a commit outlives it.
    /// </summary>
    public async Task<HistoryStep?> CommittedAsync()
    {
        if (_recorded is not { } recorded || _committed)
            return null;
        _committed = true;
        _scope?.Moved(recorded);
        await _history.SnapshotIfDueAsync(_folder);
        return recorded.Landed ?? recorded.Changed;
    }

    /// <summary>
    /// Announces what the commit did to History: the steps it closed, then the step it changed or
    /// landed. After the lock is released, and only once <see cref="CommittedAsync"/> ran.
    /// </summary>
    public void Announce()
    {
        if (_committed && _recorded is { } recorded)
            _history.Announce(_folder, recorded);
    }

    private async Task<RecordedStep?> RecordStepAsync(
        ProjectStateMutation mutation, BookMutationEffects effects, CancellationToken ct)
    {
        var (scope, runKind) = (_scope, _runKind);
        var current = scope?.Current;
        var patchset = _session.Patchset();
        var projectFolder = Path.GetDirectoryName(_connection.DataSource)!;
        var files = mutation.RecordsFileChanges ?? await StateFiles.ChangesAsync(
            _db, projectFolder, HistoryDatabase.FolderOf(projectFolder), _tipFiles, mutation.WritesFiles, ct);
        if (patchset.Length == 0 && files.Count == 0)
            return null;
        var chunk = new HistoryDatabase.Chunk(patchset, files);

        const string h = HistoryDatabase.Schema;
        await using var read = Command($"""
            SELECT head.StepId, step.IsOpen, (SELECT COALESCE(MAX(Sequence), 0) + 1 FROM {h}.Step)
            FROM (SELECT 1) LEFT JOIN {h}.Head head ON head.Id = 1 LEFT JOIN {h}.Step step ON step.StepId = head.StepId
            """);
        Guid? head;
        bool headOpen;
        int sequence;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            head = reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0));
            headOpen = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
            sequence = reader.GetInt32(2);
        }

        var now = _clock.GetUtcNow();
        if (scope is not null && current is { } on && on.StepId == head && headOpen)
        {
            var extended = on.Changes.With(effects);
            var extendedKinds = Including(on.Kinds, runKind);
            var extendedSummary = mutation.StepSummary ?? await StepSummary.DescribeAsync(_db, extended, ct);
            var row = await HistoryDatabase.ExtendStepAsync(
                _connection, Transaction, h, on.StepId, LabelOf(scope, extendedKinds), ActionOf(scope, extendedKinds),
                extendedSummary, now, chunk, ct);
            return new RecordedStep(on.StepId, extended, extendedKinds, on.Part, Landed: null, Changed: row, Closed: []);
        }

        // A new step describes only what it holds, even when a scope carries on in it: its changes,
        // and the Background run work in it, which its label names.
        var changes = StepChanges.None.With(effects);
        var kinds = Including(new HashSet<BackgroundRunKind>(), runKind);
        var summary = mutation.StepSummary ?? await StepSummary.DescribeAsync(_db, changes, ct);
        var closed = new List<HistoryStep>();
        if (head is { } open && headOpen
            && await HistoryDatabase.CloseStepAsync(_connection, Transaction, h, open, now, note: null, ct) is { } interrupted)
            closed.Add(interrupted);

        // A scope that already has a step was interrupted, and carries on as its next part. Its
        // first part is named only now, so a scope never split has no note.
        var part = 1;
        if (current is { } previous)
        {
            part = previous.Part + 1;
            if (part == 2
                && await HistoryDatabase.CloseStepAsync(_connection, Transaction, h, previous.StepId, now, StepNotes.Part(1), ct) is { } first)
                closed.Add(first);
        }

        // A commit no scope holds is a request's edit of its own, named from the mutation.
        var step = new HistoryStep(
            Guid.NewGuid(), head, sequence, scope is null ? mutation.OwnStep?.Label ?? ProjectHistory.LabelOf(mutation) : LabelOf(scope, kinds), summary, _author, now,
            EndedAt: now, IsOpen: scope is not null, Note: StepNotes.Of(part, ending: null),
            Kind: scope?.Kind ?? HistoryStepKind.Request, Action: scope is null ? mutation.OwnStep?.Action ?? StepAction.Edit : ActionOf(scope, kinds),
            RestoredFromStepId: mutation.RestoresStepId);
        await HistoryDatabase.AppendStepAsync(_connection, Transaction, h, step, chunk, ct);
        return new RecordedStep(step.StepId, changes, kinds, part, step, Changed: null, closed);
    }

    /// <summary>A Background run's step is named for the run work in it; a request's for the request.</summary>
    private static string LabelOf(HistoryStepScope.OpenScope scope, IReadOnlySet<BackgroundRunKind> kinds) =>
        kinds.Count > 0 ? BackgroundRuns.LabelOf(kinds) : scope.Label;

    /// <summary>A Background run's action follows the run work in it too; a request's is its own.</summary>
    private static StepAction? ActionOf(HistoryStepScope.OpenScope scope, IReadOnlySet<BackgroundRunKind> kinds) =>
        kinds.Count > 0 ? BackgroundRuns.ActionOf(kinds) : scope.Action;

    private static HashSet<BackgroundRunKind> Including(IReadOnlySet<BackgroundRunKind> kinds, BackgroundRunKind? kind) =>
        kind is { } k ? [.. kinds, k] : [.. kinds];

    public async ValueTask DisposeAsync()
    {
        _session.Dispose();
        await DetachAndCloseAsync(_db, _connection);
    }

    private static async Task<List<string>> StateTablesAsync(
        SqliteConnection connection, IReadOnlyCollection<string> notState, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM main.sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("__EF", StringComparison.OrdinalIgnoreCase)
                && !notState.Contains(name, StringComparer.OrdinalIgnoreCase))
                tables.Add(name);
        }
        return tables;
    }

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = Transaction;
        return command;
    }

    /// <summary>The mutation's transaction, which a raw command on its connection must name.</summary>
    private SqliteTransaction Transaction =>
        (SqliteTransaction)_db.Database.CurrentTransaction!.GetDbTransaction();

    private static async Task DetachAndCloseAsync(ProjectDbContext db, SqliteConnection connection)
    {
        try
        {
            await using var detach = connection.CreateCommand();
            detach.CommandText = $"DETACH DATABASE {HistoryDatabase.Schema}";
            await detach.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqliteException)
        {
            // Never attached (BeginAsync failed at the ATTACH), or already gone with the connection.
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

/// <summary>
/// Where a commit landed in History: the step it is a chunk of and which part of its scope that
/// step is, the step row when the commit created it or as it stands once the commit extended it,
/// and the steps it closed or renamed "part 1" on the way — each announced once the commit stands. <c>Changes</c> is everything the step now
/// holds, which its "what changed" describes, and <c>Kinds</c> the Background run work in it, which
/// its label names.
/// </summary>
internal sealed record RecordedStep(
    Guid StepId, StepChanges Changes, IReadOnlySet<BackgroundRunKind> Kinds, int Part, HistoryStep? Landed,
    HistoryStep? Changed, IReadOnlyList<HistoryStep> Closed);
