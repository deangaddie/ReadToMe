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

    private readonly ConcurrentDictionary<string, byte> _restoring = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRestoring(ProjectFolderId folder) => _restoring.ContainsKey(folder.Value);

    /// <summary>Sets the flag; false when a Restore of the project already holds it.</summary>
    public bool TryBegin(ProjectFolderId folder) => _restoring.TryAdd(folder.Value, 0);

    public void End(ProjectFolderId folder) => _restoring.TryRemove(folder.Value, out _);
}
