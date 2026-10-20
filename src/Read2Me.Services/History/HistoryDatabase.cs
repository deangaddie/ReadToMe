using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Read2Me.Services.History;

/// <summary>
/// The layout of a project's <c>history/</c> folder and the schema of its <c>history.db</c>
/// (ADR 0016): Step, Head and Chunk. Kept in one place because two connections speak it — the
/// mutation's own, with history.db attached as <see cref="Schema"/>, and a plain one for listing.
/// </summary>
internal static class HistoryDatabase
{
    /// <summary>The name history.db is attached under on a mutation's connection.</summary>
    public const string Schema = "history";

    public const string FolderName = "history";
    public const string FileName = "history.db";

    /// <summary>The state before the first chunk: "History started", or the empty project an import fills.</summary>
    public const string BaseSnapshotName = "base.db";

    public const string StartedLabel = "History started";

    /// <summary>Bumped by any later change to the tables below, so an older history.db can be told apart.</summary>
    private const int Version = 1;

    public static string FolderOf(string projectFolderPath) => Path.Combine(projectFolderPath, FolderName);
    public static string FileIn(string historyFolder) => Path.Combine(historyFolder, FileName);
    public static string SnapshotsIn(string historyFolder) => Path.Combine(historyFolder, "snapshots");

    public static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        // Unpooled like project.db, so nothing keeps the file open once a caller is done with it:
        // deleting a project deletes its history folder.
        Pooling = false,
    }.ToString());

    /// <summary>
    /// Creates the tables, unqualified, on a connection to a new history.db. The columns the spec
    /// fixes for later tickets — a restore's source, a note ("part 2", "cancelled"), the "what
    /// changed" summary, the can't-be-opened flag and a chunk's file changes — are created now and
    /// left null, so a history.db written today needs no upgrade when they are filled.
    /// </summary>
    public static void CreateSchema(SqliteConnection connection)
    {
        Execute(connection, $"""
            CREATE TABLE Step (
                StepId TEXT NOT NULL PRIMARY KEY,
                ParentStepId TEXT NULL,
                Sequence INTEGER NOT NULL UNIQUE,
                Label TEXT NOT NULL,
                Author TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                IsOpen INTEGER NOT NULL,
                RestoredFromStepId TEXT NULL,
                Note TEXT NULL,
                Summary TEXT NULL,
                CantOpen INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE Head (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                StepId TEXT NOT NULL
            );
            CREATE TABLE Chunk (
                StepId TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                Patchset BLOB NOT NULL,
                FileChanges TEXT NULL,
                PRIMARY KEY (StepId, Sequence)
            );
            PRAGMA user_version = {Version};
            """);
    }

    /// <summary>
    /// Appends <paramref name="step"/> at the tip — its row, the chunk when there is a patchset, and
    /// the Head moved onto it. <paramref name="schema"/> qualifies the tables: the attached name on
    /// a mutation's connection, or empty on a connection to history.db itself.
    /// </summary>
    public static async Task AppendStepAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string schema, HistoryStep step, byte[]? patchset,
        CancellationToken ct)
    {
        var q = schema.Length == 0 ? "" : schema + ".";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {q}Step ({StepColumns})
                VALUES ($id, $parent, $sequence, $label, $author, $startedAt, $endedAt, $isOpen);
            {(patchset is null ? "" : $"INSERT INTO {q}Chunk (StepId, Sequence, Patchset) VALUES ($id, 1, $patchset);")}
            INSERT INTO {q}Head (Id, StepId) VALUES (1, $id) ON CONFLICT (Id) DO UPDATE SET StepId = excluded.StepId;
            """;
        command.Parameters.AddWithValue("$id", Stored(step.StepId));
        command.Parameters.AddWithValue("$parent", step.ParentStepId is { } p ? Stored(p) : DBNull.Value);
        command.Parameters.AddWithValue("$sequence", step.Sequence);
        command.Parameters.AddWithValue("$label", step.Label);
        command.Parameters.AddWithValue("$author", step.Author);
        command.Parameters.AddWithValue("$startedAt", Stored(step.StartedAt));
        command.Parameters.AddWithValue("$endedAt", step.EndedAt is { } e ? Stored(e) : DBNull.Value);
        command.Parameters.AddWithValue("$isOpen", step.IsOpen ? 1 : 0);
        if (patchset is not null)
            command.Parameters.AddWithValue("$patchset", patchset);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Stored(Guid id) => id.ToString("D");
    private static string Stored(DateTimeOffset at) => at.ToString("O");

    public static HistoryStep ReadStep(DbDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
        r.GetInt32(2),
        r.GetString(3),
        r.GetString(4),
        DateTimeOffset.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.GetInt64(7) != 0);

    /// <summary>The columns <see cref="ReadStep"/> reads, in its order.</summary>
    public const string StepColumns = "StepId, ParentStepId, Sequence, Label, Author, StartedAt, EndedAt, IsOpen";

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
