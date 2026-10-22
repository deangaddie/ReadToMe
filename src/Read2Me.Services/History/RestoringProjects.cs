using System.Collections.Concurrent;
using Read2Me.Core.Models;

namespace Read2Me.Services.History;

/// <summary>
/// The per-project <b>restoring flag</b> (spec, "Busy and restoring"): set for the whole of a
/// Restore, so every other write and enqueue for the project is refused at once with "Project is
/// restoring" rather than waiting on the write lock. At most one Restore runs per project.
/// </summary>
public sealed class RestoringProjects
{
    public const string Reason = "Project is restoring";

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _restoring = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRestoring(ProjectFolderId folder) => _restoring.ContainsKey(folder.Value);

    /// <summary>Sets the flag; false when a Restore of the project already holds it.</summary>
    public bool TryBegin(ProjectFolderId folder) =>
        _restoring.TryAdd(folder.Value, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void End(ProjectFolderId folder)
    {
        if (_restoring.TryRemove(folder.Value, out var ended))
            ended.TrySetResult();
    }

    /// <summary>Completes when the project's Restore ends; already complete when none is running.</summary>
    public Task WhenEnded(ProjectFolderId folder) =>
        _restoring.TryGetValue(folder.Value, out var running) ? running.Task : Task.CompletedTask;
}
