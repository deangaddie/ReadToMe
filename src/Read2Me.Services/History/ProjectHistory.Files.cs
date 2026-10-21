using Microsoft.Data.Sqlite;
using Read2Me.Core.Models;

namespace Read2Me.Services.History;

public sealed partial class ProjectHistory
{
    /// <summary>
    /// A state file — a Voice's reference audio or stored original, the cover, the source book file
    /// — as it stood after <paramref name="stepId"/>: its copy in the blobs, or missing when the
    /// file didn't exist then. <paramref name="relativePath"/> is project-relative, as the rows name
    /// it. Any other file is never in a step, so it is missing too.
    /// </summary>
    public async Task<StepFile> ReadFileAsync(
        ProjectFolderId folder, Guid stepId, string relativePath, CancellationToken ct = default)
    {
        var file = HistoryFileOf(folder);
        if (!File.Exists(file))
            return new StepFile.UnknownStep();

        var historyFolder = Path.GetDirectoryName(file)!;
        HistoryStep? step;
        Dictionary<string, string?> files;
        await using (var connection = HistoryDatabase.Open(file, SqliteOpenMode.ReadOnly))
        {
            await connection.OpenAsync(ct);
            await using var read = await connection.BeginTransactionAsync(ct);
            step = await HistoryDatabase.ReadStepAsync(connection, stepId, ct);
            if (step is null)
                return new StepFile.UnknownStep();
            files = await FilesAtAsync(connection, historyFolder, stepId, ct);
        }

        return files.GetValueOrDefault(StateFiles.Normalise(relativePath)) is { } hash
               && StateFiles.BlobOf(historyFolder, hash) is var blob && File.Exists(blob)
            ? new StepFile.Found(blob, step.IsOpen)
            : new StepFile.Missing();
    }

    /// <summary>
    /// The state files' list after <paramref name="stepId"/>, or before the first step for none:
    /// the nearest earlier snapshot's list with the file changes of every chunk after it applied.
    /// </summary>
    private static async Task<Dictionary<string, string?>> FilesAtAsync(
        SqliteConnection connection, string historyFolder, Guid? stepId, CancellationToken ct)
    {
        if (stepId is not { } id)
            return await StateFiles.ReadListAsync(HistoryDatabase.SnapshotOf(historyFolder, null), ct);

        var line = await HistoryDatabase.ReadLineAsync(connection, id, ct);
        var (depth, snapshot) = NearestSnapshot(historyFolder, line);
        var files = await StateFiles.ReadListAsync(snapshot, ct);
        foreach (var changes in await HistoryDatabase.ReadFileChangesAsync(connection, id, depth, ct))
            StateFiles.Apply(files, changes);
        return files;
    }

    /// <summary>The state files' list at the tip: what the next commit's file changes are taken against.</summary>
    private static async Task<Dictionary<string, string?>> TipFilesAsync(string historyFolder, CancellationToken ct)
    {
        await using var connection = HistoryDatabase.Open(HistoryDatabase.FileIn(historyFolder), SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        var head = await HistoryDatabase.ReadHeadAsync(connection, ct);
        return await FilesAtAsync(connection, historyFolder, head?.StepId, ct);
    }
}

/// <summary>What reading a state file at a History step answered.</summary>
public abstract record StepFile
{
    private StepFile() { }

    /// <summary>
    /// The file as it was then, at <paramref name="Path"/>, its copy in the blobs. Read it, never
    /// write it. <paramref name="StepIsOpen"/>: the step can still grow, so what it holds may change.
    /// </summary>
    public sealed record Found(string Path, bool StepIsOpen) : StepFile;

    /// <summary>The file didn't exist at that step, or isn't a state file.</summary>
    public sealed record Missing : StepFile;

    /// <summary>The project's History has no step with that id (or the project has no History yet).</summary>
    public sealed record UnknownStep : StepFile;
}
