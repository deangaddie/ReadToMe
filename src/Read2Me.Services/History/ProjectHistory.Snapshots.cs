using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data;

namespace Read2Me.Services.History;

public sealed partial class ProjectHistory
{
    /// <summary>
    /// The project folders whose History this process has seen recording at the live schema, as a
    /// set. The schema only changes when a new build migrates a project on open, so once checked, a
    /// folder stays checked for the life of the process.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _atLiveSchema = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Takes a snapshot of the tip once <see cref="HistoryOptions.SnapshotEvery"/> closed steps have
    /// landed since the nearest earlier one, so opening a step replays at most that many steps. The
    /// caller holds the project's write lock and has just committed or closed a step. A tip still
    /// open waits for its close. Best-effort: a failed snapshot is retried after the next step.
    /// </summary>
    internal async Task SnapshotIfDueAsync(ProjectFolderId folder)
    {
        var projectFolder = fs.GetProjectFolderPath(folder);
        var historyFolder = HistoryDatabase.FolderOf(projectFolder);
        try
        {
            Guid head;
            Dictionary<string, string?> files;
            await using (var connection = HistoryDatabase.Open(HistoryDatabase.FileIn(historyFolder), SqliteOpenMode.ReadOnly))
            {
                await connection.OpenAsync(CancellationToken.None);
                if (await HistoryDatabase.ReadHeadAsync(connection, CancellationToken.None) is not ({ } tip, false))
                    return;
                head = tip;
                var line = await HistoryDatabase.ReadLineAsync(connection, head, CancellationToken.None);
                if (NearestSnapshot(historyFolder, line).Depth < options.Value.SnapshotEvery)
                    return;
                files = await FilesAtAsync(connection, historyFolder, head, CancellationToken.None);
            }

            await TakeSnapshotAsync(Path.Combine(projectFolder, ProjectDbContextProvider.DbFileName), historyFolder, head, files, CancellationToken.None);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Taking a History snapshot of {Folder} failed; the next closed step tries again.", folder.Value);
        }
    }

    /// <summary>
    /// Makes sure the chunks recorded from here on are at the live schema: when the project was
    /// migrated since its nearest snapshot (a new build opened it), closes any step left open and
    /// snapshots the tip as it now stands, so no step's chunks span the schema change. Steps before
    /// it open from the older snapshot and are migrated as they open. The caller holds the
    /// project's write lock, and <paramref name="db"/> is open and migrated.
    /// <para>
    /// A check that fails is logged and tried again on the next commit, rather than refusing the
    /// commit: losing a write is worse than a step that may not open later.
    /// </para>
    /// </summary>
    private async Task SnapshotIfMigratedAsync(ProjectFolderId folder, ProjectDbContext db, CancellationToken ct)
    {
        if (_atLiveSchema.ContainsKey(folder.Value))
            return;

        var historyFolder = HistoryFolderOf(db);
        var projectDb = ((SqliteConnection)db.Database.GetDbConnection()).DataSource;
        IReadOnlyList<HistoryStep> closed = [];
        try
        {
            await using var connection = HistoryDatabase.Open(HistoryDatabase.FileIn(historyFolder), SqliteOpenMode.ReadWrite);
            await connection.OpenAsync(ct);
            var head = await HistoryDatabase.ReadHeadAsync(connection, ct);
            var line = head is { } h ? await HistoryDatabase.ReadLineAsync(connection, h.StepId, ct) : [];
            if (await LastMigrationAsync(NearestSnapshot(historyFolder, line).File, ct) != await LastMigrationAsync(projectDb, ct))
            {
                logger.LogInformation(
                    "{Folder} was migrated since its last History snapshot; snapshotting it at the new schema.", folder.Value);
                var files = await FilesAtAsync(connection, historyFolder, head?.StepId, ct);
                closed = await HistoryDatabase.CloseInterruptedAsync(connection, ct);
                await TakeSnapshotAsync(projectDb, historyFolder, head?.StepId, files, ct);
            }
            _atLiveSchema[folder.Value] = true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Checking {Folder}'s History against its schema failed; the next commit tries again.", folder.Value);
        }
        foreach (var step in closed)
            events.Publish(new HistoryStepEvent(folder, HistoryStepEventKind.Closed, step));
    }

    /// <summary>
    /// The nearest snapshot along <paramref name="line"/> (the step first): the depth of the
    /// nearest step with one, or the line's length when there is none and the base snapshot is the
    /// nearest — as many steps as replay onto it — and its file.
    /// </summary>
    private static (int Depth, string File) NearestSnapshot(string historyFolder, List<Guid> line)
    {
        for (var depth = 0; depth < line.Count; depth++)
        {
            var file = HistoryDatabase.SnapshotOf(historyFolder, line[depth]);
            if (File.Exists(file))
                return (depth, file);
        }
        return (line.Count, HistoryDatabase.SnapshotOf(historyFolder, null));
    }

    /// <summary>
    /// <c>VACUUM INTO</c> a temporary file and moved into place, so a snapshot that exists is
    /// always whole. Its own <c>__EFMigrationsHistory</c> tags it with its schema. The state files'
    /// <paramref name="files"/> list is written beside it first, so a snapshot always has its list.
    /// </summary>
    private static async Task TakeSnapshotAsync(
        string projectDb, string historyFolder, Guid? stepId, Dictionary<string, string?> files, CancellationToken ct)
    {
        var target = HistoryDatabase.SnapshotOf(historyFolder, stepId);
        await StateFiles.WriteListAsync(target, files, ct);
        var pending = target + ".tmp";
        File.Delete(pending); // a leftover from a snapshot that never finished; VACUUM INTO will not overwrite
        await using (var connection = HistoryDatabase.Open(projectDb, SqliteOpenMode.ReadOnly))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", pending);
            await command.ExecuteNonQueryAsync(ct);
        }
        File.Move(pending, target, overwrite: true);
    }

    /// <summary>The last migration applied to a project database or snapshot: its schema.</summary>
    private static async Task<string?> LastMigrationAsync(string databaseFile, CancellationToken ct)
    {
        await using var connection = HistoryDatabase.Open(databaseFile, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(MigrationId) FROM __EFMigrationsHistory";
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
