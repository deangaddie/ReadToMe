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

    /// <summary>
    /// Bumped by any later change to what history.db holds, so an older one can be told apart: 2 is
    /// the first whose chunks and snapshots record the state files.
    /// </summary>
    public const int Version = 2;

    public static string FolderOf(string projectFolderPath) => Path.Combine(projectFolderPath, FolderName);
    public static string FileIn(string historyFolder) => Path.Combine(historyFolder, FileName);
    public static string SnapshotsIn(string historyFolder) => Path.Combine(historyFolder, "snapshots");

    /// <summary>The snapshot of the state after <paramref name="stepId"/>, or the base one for none.</summary>
    public static string SnapshotOf(string historyFolder, Guid? stepId) =>
        Path.Combine(SnapshotsIn(historyFolder), stepId is { } id ? $"{id:N}.db" : BaseSnapshotName);

    /// <summary>Throwaway materialised steps: deleting the folder loses nothing.</summary>
    public static string CacheIn(string historyFolder) => Path.Combine(historyFolder, "cache");

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

    /// <summary>The <c>user_version</c> of an existing history.db: which <see cref="Version"/> wrote it.</summary>
    public static async Task<long> ReadVersionAsync(string file, CancellationToken ct)
    {
        await using var connection = Open(file, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Appends <paramref name="step"/> at the tip — its row, the chunk when there is one, and the
    /// Head moved onto it. <paramref name="schema"/> qualifies the tables: the attached name on a
    /// mutation's connection, or empty on a connection to history.db itself.
    /// </summary>
    public static async Task AppendStepAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string schema, HistoryStep step, Chunk? chunk,
        CancellationToken ct)
    {
        var q = Qualifier(schema);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {q}Step ({InsertedColumns})
                VALUES ($id, $parent, $sequence, $label, $summary, $author, $startedAt, $endedAt, $isOpen, $note);
            {(chunk is null ? "" : $"INSERT INTO {q}Chunk (StepId, Sequence, Patchset, FileChanges) VALUES ($id, 1, $patchset, $files);")}
            INSERT INTO {q}Head (Id, StepId) VALUES (1, $id) ON CONFLICT (Id) DO UPDATE SET StepId = excluded.StepId;
            """;
        command.Parameters.AddWithValue("$id", Stored(step.StepId));
        command.Parameters.AddWithValue("$parent", step.ParentStepId is { } p ? Stored(p) : DBNull.Value);
        command.Parameters.AddWithValue("$sequence", step.Sequence);
        command.Parameters.AddWithValue("$label", step.Label);
        command.Parameters.AddWithValue("$summary", (object?)step.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$author", step.Author);
        command.Parameters.AddWithValue("$startedAt", Stored(step.StartedAt));
        command.Parameters.AddWithValue("$endedAt", step.EndedAt is { } e ? Stored(e) : DBNull.Value);
        command.Parameters.AddWithValue("$isOpen", step.IsOpen ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)step.Note ?? DBNull.Value);
        if (chunk is not null)
            AddChunkParameters(command, chunk);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddChunkParameters(SqliteCommand command, Chunk chunk)
    {
        command.Parameters.AddWithValue("$patchset", chunk.Patchset);
        command.Parameters.AddWithValue("$files", chunk.FileChanges is { Count: > 0 } files ? StateFiles.Serialise(files) : DBNull.Value);
    }

    /// <summary>
    /// Appends <paramref name="chunk"/> as the next chunk of the open step <paramref name="stepId"/>
    /// and rewrites its label — a Background run's grows when another run's work lands in it — its
    /// "what changed", and its end time: an open step's is its last change, <paramref name="at"/>.
    /// The Head already points at it. Answers the step's row as it now stands.
    /// </summary>
    public static async Task<HistoryStep> ExtendStepAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string schema, Guid stepId, string label,
        string? summary, DateTimeOffset at, Chunk chunk, CancellationToken ct)
    {
        var q = Qualifier(schema);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {q}Chunk (StepId, Sequence, Patchset, FileChanges)
                VALUES ($id, (SELECT MAX(Sequence) + 1 FROM {q}Chunk WHERE StepId = $id), $patchset, $files);
            UPDATE {q}Step SET Label = $label, Summary = $summary, EndedAt = $at WHERE StepId = $id
                RETURNING {StepColumns};
            """;
        command.Parameters.AddWithValue("$id", Stored(stepId));
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", Stored(at));
        AddChunkParameters(command, chunk);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadStep(reader);
    }

    /// <summary>
    /// Closes <paramref name="stepId"/> at <paramref name="at"/>, setting its <paramref name="note"/>
    /// when one is given, and answers its row as it now stands — or null when it was already closed
    /// and there is no note to set, so a change is announced once. A step closed earlier keeps the
    /// time it closed at.
    /// </summary>
    public static async Task<HistoryStep?> CloseStepAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string schema, Guid stepId, DateTimeOffset at,
        string? note, CancellationToken ct)
    {
        var q = Qualifier(schema);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {q}Step SET IsOpen = 0, EndedAt = IIF(IsOpen = 1, $at, EndedAt), Note = COALESCE($note, Note)
                WHERE StepId = $id AND (IsOpen = 1 OR $note IS NOT NULL)
                RETURNING {StepColumns};
            """;
        command.Parameters.AddWithValue("$id", Stored(stepId));
        command.Parameters.AddWithValue("$at", Stored(at));
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStep(reader) : null;
    }

    /// <summary>
    /// Closes every step still open as "interrupted", keeping the end time of its last change, and
    /// answers their rows: what the app does on start, when nothing that opened them is still running.
    /// </summary>
    public static async Task<IReadOnlyList<HistoryStep>> CloseInterruptedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE Step SET IsOpen = 0, Note = IIF(Note IS NULL, $interrupted, Note || $separator || $interrupted)
                WHERE IsOpen = 1
                RETURNING {StepColumns};
            """;
        command.Parameters.AddWithValue("$interrupted", StepNotes.Interrupted);
        command.Parameters.AddWithValue("$separator", StepNotes.Separator);
        var closed = new List<HistoryStep>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            closed.Add(ReadStep(reader));
        return closed;
    }

    /// <summary>One step's row, on a connection to history.db itself; null when there is no such step.</summary>
    public static async Task<HistoryStep?> ReadStepAsync(SqliteConnection connection, Guid stepId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {StepColumns} FROM Step WHERE StepId = $id";
        command.Parameters.AddWithValue("$id", Stored(stepId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStep(reader) : null;
    }

    /// <summary>
    /// Marks <paramref name="stepId"/> can't-be-opened, on a connection to history.db itself, and
    /// answers its row as it now stands; null when there is no such step.
    /// </summary>
    public static async Task<HistoryStep?> MarkCantOpenAsync(SqliteConnection connection, Guid stepId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE Step SET CantOpen = 1 WHERE StepId = $id RETURNING {StepColumns}";
        command.Parameters.AddWithValue("$id", Stored(stepId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStep(reader) : null;
    }

    /// <summary>The Head's step and whether it is still open; null on a History with no steps yet.</summary>
    public static async Task<(Guid StepId, bool IsOpen)?> ReadHeadAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT head.StepId, step.IsOpen FROM Head head JOIN Step step ON step.StepId = head.StepId WHERE head.Id = 1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (Guid.Parse(reader.GetString(0)), reader.GetInt64(1) != 0) : null;
    }

    /// <summary>
    /// <paramref name="stepId"/> and each step along its parents to the root, the step itself
    /// first: index <c>i</c> is the step at depth <c>i</c>.
    /// </summary>
    public static async Task<List<Guid>> ReadLineAsync(SqliteConnection connection, Guid stepId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{LineOf} SELECT StepId FROM line ORDER BY Depth";
        command.Parameters.AddWithValue("$id", Stored(stepId));
        var line = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            line.Add(Guid.Parse(reader.GetString(0)));
        return line;
    }

    /// <summary>
    /// Every chunk of the steps along <paramref name="stepId"/>'s line shallower than
    /// <paramref name="depth"/>, the depth of the snapshot they replay onto. <c>Depth</c> is 0 for
    /// the step itself, 1 for its parent and so on: replay deepest first, each step's chunks in
    /// sequence.
    /// </summary>
    public static async Task<List<(int Depth, int Sequence, byte[] Patchset)>> ReadChunksAsync(
        SqliteConnection connection, Guid stepId, int depth, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {LineOf}
            SELECT line.Depth, c.Sequence, c.Patchset FROM line JOIN Chunk c ON c.StepId = line.StepId
                WHERE line.Depth < $depth
            """;
        command.Parameters.AddWithValue("$id", Stored(stepId));
        command.Parameters.AddWithValue("$depth", depth);
        var chunks = new List<(int, int, byte[])>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            chunks.Add((reader.GetInt32(0), reader.GetInt32(1), (byte[])reader.GetValue(2)));
        return chunks;
    }

    /// <summary>
    /// The state file changes of the chunks <see cref="ReadChunksAsync"/> reads, in replay order:
    /// deepest first, each step's in sequence. Chunks that changed no file are left out.
    /// </summary>
    public static async Task<List<Dictionary<string, string?>>> ReadFileChangesAsync(
        SqliteConnection connection, Guid stepId, int depth, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {LineOf}
            SELECT c.FileChanges FROM line JOIN Chunk c ON c.StepId = line.StepId
                WHERE line.Depth < $depth AND c.FileChanges IS NOT NULL
                ORDER BY line.Depth DESC, c.Sequence
            """;
        command.Parameters.AddWithValue("$id", Stored(stepId));
        command.Parameters.AddWithValue("$depth", depth);
        var changes = new List<Dictionary<string, string?>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            changes.Add(StateFiles.Deserialise(reader.GetString(0)));
        return changes;
    }

    /// <summary>The step <c>$id</c> and its parents to the root, each with its depth below it.</summary>
    private const string LineOf = """
        WITH RECURSIVE line (StepId, ParentStepId, Depth) AS (
            SELECT StepId, ParentStepId, 0 FROM Step WHERE StepId = $id
            UNION ALL
            SELECT s.StepId, s.ParentStepId, line.Depth + 1 FROM Step s JOIN line ON s.StepId = line.ParentStepId
        )
        """;

    /// <summary>"history." for the attached name, nothing on a connection to history.db itself.</summary>
    private static string Qualifier(string schema) => schema.Length == 0 ? "" : schema + ".";

    private static string Stored(Guid id) => id.ToString("D");
    private static string Stored(DateTimeOffset at) => at.ToString("O");

    public static HistoryStep ReadStep(DbDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
        r.GetInt32(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5),
        DateTimeOffset.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.GetInt64(8) != 0,
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : Guid.Parse(r.GetString(10)),
        r.GetInt64(11) != 0);

    /// <summary>The columns <see cref="ReadStep"/> reads, in its order.</summary>
    public const string StepColumns = InsertedColumns + ", RestoredFromStepId, CantOpen";

    /// <summary>The columns <see cref="AppendStepAsync"/> writes; the rest keep their defaults until a later ticket sets them.</summary>
    private const string InsertedColumns = "StepId, ParentStepId, Sequence, Label, Summary, Author, StartedAt, EndedAt, IsOpen, Note";

    /// <summary>
    /// One commit's record: the patchset of its rows, empty when only files changed, and the state
    /// files it changed, path to new hash or null for gone.
    /// </summary>
    public sealed record Chunk(byte[] Patchset, Dictionary<string, string?>? FileChanges);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
