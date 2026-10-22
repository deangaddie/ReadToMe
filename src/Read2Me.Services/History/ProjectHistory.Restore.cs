using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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
        return new RestoreWork(copy, files);
    }

    /// <summary>
    /// Makes the project's state files as the Head's file list has them: each listed file whose
    /// content differs is written from the blobs, and each the Head's own changes say is gone is
    /// deleted. What a Restore does once it has committed, so the rows are the truth and the files
    /// follow them. Results — takes, the audiobook — are never touched. A file that can't be written
    /// is logged and left for the repair at the next app start. False, writing nothing, when the
    /// write lock stayed taken past the mutation budget.
    /// </summary>
    internal async Task<bool> WriteStateFilesAsync(ProjectFolderId folder, CancellationToken ct)
    {
        using var writeLock = await writeLocks.AcquireAsync(folder, mutationOptions.Value.LockWaitBudget, ct);
        if (writeLock is null)
            return false;
        await WriteStateFilesUnderLockAsync(folder, ct);
        return true;
    }

    /// <summary>
    /// The repair app start does, when no Restore runs: the state files are made as the Head lists
    /// them (<see cref="WriteStateFilesAsync"/>), finishing a Restore that committed but didn't get
    /// to write them all, and what a Restore leaves in <c>history/cache/</c> when the process dies
    /// mid-way is deleted — its <c>*.restoring</c> copy, and the <c>*.aside</c> folders an older
    /// build moved files into. A project with no History has nothing to repair.
    /// </summary>
    public async Task RepairAsync(ProjectFolderId folder, CancellationToken ct = default)
    {
        var historyFile = HistoryFileOf(folder);
        if (!File.Exists(historyFile))
            return;

        using var writeLock = await writeLocks.AcquireAsync(folder, mutationOptions.Value.LockWaitBudget, ct);
        if (writeLock is null)
            return; // something is already writing; the repair waits for the next start

        var cache = HistoryDatabase.CacheIn(Path.GetDirectoryName(historyFile)!);
        if (Directory.Exists(cache))
        {
            foreach (var copy in Directory.GetFiles(cache, "*.restoring"))
                TryRemove(() => File.Delete(copy), copy);
            foreach (var aside in Directory.GetDirectories(cache, "*.aside"))
                TryRemove(() => Directory.Delete(aside, recursive: true), aside);
        }
        await WriteStateFilesUnderLockAsync(folder, ct);
    }

    private async Task WriteStateFilesUnderLockAsync(ProjectFolderId folder, CancellationToken ct)
    {
        var historyFile = HistoryFileOf(folder);
        if (!File.Exists(historyFile))
            return;

        var historyFolder = Path.GetDirectoryName(historyFile)!;
        FileList listed;
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = HistoryDatabase.Open(historyFile, SqliteOpenMode.ReadOnly))
        {
            await connection.OpenAsync(ct);
            await using var read = await connection.BeginTransactionAsync(ct);
            var head = await HistoryDatabase.ReadHeadAsync(connection, ct);
            listed = await FilesAtAsync(connection, historyFolder, head?.StepId, ct);
            if (head is { } h)
                foreach (var changes in await HistoryDatabase.ReadFileChangesAsync(connection, h.StepId, depth: 1, ct))
                    gone.UnionWith(changes.Where(c => c.Value is null).Select(c => c.Key));
        }

        var projectFolder = fs.GetProjectFolderPath(folder);
        foreach (var path in listed.Paths)
        {
            try
            {
                await StateFiles.WriteFromBlobAsync(projectFolder, historyFolder, path, listed.HashOf(path)!, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "State file {Path} of {Folder} couldn't be written as History lists it; the repair at the next app start tries again.", path, folder.Value);
            }
        }
        foreach (var path in gone.Where(p => !listed.Contains(p)))
            TryRemove(() => File.Delete(Path.Combine(projectFolder, path)), path);
    }

    /// <summary>Removes a file or folder, or logs and carries on when it can't: the next repair tries again.</summary>
    private void TryRemove(Action remove, string path)
    {
        try
        {
            remove();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "{Path} couldn't be removed; the repair at the next app start tries again.", path);
        }
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
