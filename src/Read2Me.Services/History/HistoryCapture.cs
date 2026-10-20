using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Read2Me.Data;

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
    /// Appends what the session recorded as one closed step with one chunk, at the tip, inside the
    /// mutation's transaction. Returns null, recording nothing, when no state table changed.
    /// </summary>
    public async Task<HistoryStep?> RecordAsync(string label, CancellationToken ct)
    {
        var patchset = _session.Patchset();
        if (patchset.Length == 0)
            return null;

        const string h = HistoryDatabase.Schema;
        await using var read = Command($"""
            SELECT (SELECT StepId FROM {h}.Head WHERE Id = 1), (SELECT COALESCE(MAX(Sequence), 0) + 1 FROM {h}.Step)
            """);
        Guid? parent;
        int sequence;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            parent = reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0));
            sequence = reader.GetInt32(1);
        }

        var now = _clock.GetUtcNow();
        var step = new HistoryStep(Guid.NewGuid(), parent, sequence, label, _author, now, now, IsOpen: false);
        await HistoryDatabase.AppendStepAsync(_connection, Transaction, h, step, patchset, ct);
        return step;
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
