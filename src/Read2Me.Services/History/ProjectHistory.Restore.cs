using Microsoft.Data.Sqlite;
using Read2Me.Core.Models;
using Read2Me.Data;

namespace Read2Me.Services.History;

public sealed partial class ProjectHistory
{
    /// <summary>
    /// The <b>Restore preview</b>: how many items would lose their audio if the project were
    /// restored to <paramref name="target"/> now — those with audio that the step doesn't have, or
    /// has with other text, speaker or delivery cue. Read without the write lock, so a commit
    /// landing meanwhile can move it; the Restore counts again under the lock.
    /// </summary>
    public async Task<int> CountRestoreAudioDiscardsAsync(ProjectFolderId folder, StepState target, CancellationToken ct = default)
    {
        await using var connection = HistoryDatabase.Open(target.DatabaseFile, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        await RestoreAudio.AttachLiveAsync(connection, ProjectDatabaseOf(folder), ct);
        return await RestoreAudio.CountDiscardsAsync(connection, ct);
    }

    /// <summary>
    /// What a Restore to <paramref name="target"/> works from: a private copy of the step's
    /// materialised state, which the Restore may write, and the step's state files list. The
    /// caller disposes it, which deletes the copy.
    /// </summary>
    internal async Task<RestoreWork> PrepareRestoreAsync(ProjectFolderId folder, StepState target, CancellationToken ct)
    {
        var historyFile = HistoryFileOf(folder);
        var historyFolder = Path.GetDirectoryName(historyFile)!;
        FileList files;
        await using (var connection = HistoryDatabase.Open(historyFile, SqliteOpenMode.ReadOnly))
        {
            await connection.OpenAsync(ct);
            files = await FilesAtAsync(connection, historyFolder, target.Step.StepId, ct);
        }

        // Not *.db, so pruning the cache never takes it while the Restore works.
        var copy = Path.Combine(HistoryDatabase.CacheIn(historyFolder), $"{Guid.NewGuid():N}.restoring");
        File.Copy(target.DatabaseFile, copy);
        return new RestoreWork(copy, files, historyFolder);
    }

    /// <summary>The step the Head points at, or null before the first step.</summary>
    internal async Task<HistoryStep?> HeadStepAsync(ProjectFolderId folder, CancellationToken ct)
    {
        await using var connection = HistoryDatabase.Open(HistoryFileOf(folder), SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        return await HistoryDatabase.ReadHeadAsync(connection, ct) is ({ } head, _)
            ? await HistoryDatabase.ReadStepAsync(connection, head, ct)
            : null;
    }

    private string ProjectDatabaseOf(ProjectFolderId folder) =>
        Path.Combine(fs.GetProjectFolderPath(folder), ProjectDbContextProvider.DbFileName);
}
