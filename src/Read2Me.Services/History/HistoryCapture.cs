using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Read2Me.Data;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.History;

/// <summary>
/// Records one state mutation as a History step, on the mutation's own connection (ADR 0016).
/// <para>
/// Begun before the mutation's transaction: ATTACH cannot run inside one, and the session must be
/// recording before the first write. <see cref="RecordAsync"/> runs after <c>SaveChanges</c> and
/// before the commit, so the step and the change it records commit or roll back together — the
/// attached history.db shares the transaction. Disposed after the transaction ends.
/// </para>
/// </summary>
internal sealed class HistoryCapture : IAsyncDisposable
{
    private readonly ProjectDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly SqliteSession _session;
    private readonly string _author;
    private readonly TimeProvider _clock;

    private HistoryCapture(
        ProjectDbContext db, SqliteConnection connection, SqliteSession session, string author, TimeProvider clock)
    {
        _db = db;
        _connection = connection;
        _session = session;
        _author = author;
        _clock = clock;
    }

    /// <summary>
    /// Attaches <paramref name="historyFile"/> and starts a session over every project table except
    /// <paramref name="notState"/> and SQLite's and EF's own bookkeeping. The connection is held open
    /// until disposal, because a session lives on one native handle.
    /// </summary>
    public static async Task<HistoryCapture> BeginAsync(
        ProjectDbContext db, string historyFile, IReadOnlyCollection<string> notState, string author, TimeProvider clock,
        CancellationToken ct)
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
            return new HistoryCapture(db, connection, session, author, clock);
        }
        catch
        {
            session?.Dispose();
            await DetachAndCloseAsync(db, connection);
            throw;
        }
    }

    /// <summary>
    /// Records what the session captured, inside the mutation's transaction, and answers the step it
    /// landed in — or null, recording nothing, when no state table changed.
    /// <para>
    /// Inside a step scope whose step is still the open tip, the change is that step's next chunk.
    /// Otherwise it starts a new step at the tip: open for the scope to extend and close, or closed
    /// at once when no scope is open. A step left open at the tip by anyone else — a scope that
    /// could not close it, or one this commit interleaves with — is closed first, so a step's chunks
    /// are always contiguous in the line.
    /// </para>
    /// </summary>
    public async Task<RecordedStep?> RecordAsync(
        HistoryStepScope.OpenScope? scope, string label, BookMutationEffects effects, CancellationToken ct)
    {
        var patchset = _session.Patchset();
        if (patchset.Length == 0)
            return null;

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

        if (scope?.StepId is { } scoped && scoped == head && headOpen)
        {
            var extended = scope.Changes.With(effects);
            var extendedSummary = await StepSummary.DescribeAsync(_db, extended, ct);
            await HistoryDatabase.ExtendStepAsync(_connection, Transaction, h, scoped, scope.Label, extendedSummary, patchset, ct);
            return new RecordedStep(scoped, extended, scope.Part, Landed: null, Closed: []);
        }

        // A new step describes only what it holds, even when a scope carries on in it.
        var changes = StepChanges.None.With(effects);
        var summary = await StepSummary.DescribeAsync(_db, changes, ct);
        var now = _clock.GetUtcNow();
        var closed = new List<HistoryStep>();
        if (head is { } open && headOpen
            && await HistoryDatabase.CloseStepAsync(_connection, Transaction, h, open, now, note: null, ct) is { } interrupted)
            closed.Add(interrupted);

        // A scope that already has a step was interrupted, and carries on as its next part. Its
        // first part is named only now, so a scope never split has no note.
        var part = 1;
        if (scope is { StepId: { } previous })
        {
            part = scope.Part + 1;
            if (part == 2
                && await HistoryDatabase.CloseStepAsync(_connection, Transaction, h, previous, now, StepNotes.Part(1), ct) is { } first)
                closed.Add(first);
        }

        var step = new HistoryStep(
            Guid.NewGuid(), head, sequence, scope?.Label ?? label, summary, _author, now,
            EndedAt: scope is null ? now : null, IsOpen: scope is not null, Note: StepNotes.Of(part, ending: null));
        await HistoryDatabase.AppendStepAsync(_connection, Transaction, h, step, patchset, ct);
        return new RecordedStep(step.StepId, changes, part, step, closed);
    }

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
/// step is, the step row when the commit created it, and the steps it closed or renamed "part 1" on
/// the way — each announced once the commit stands. <c>Changes</c> is everything the step now
/// holds, which its "what changed" describes.
/// </summary>
internal sealed record RecordedStep(
    Guid StepId, StepChanges Changes, int Part, HistoryStep? Landed, IReadOnlyList<HistoryStep> Closed);
