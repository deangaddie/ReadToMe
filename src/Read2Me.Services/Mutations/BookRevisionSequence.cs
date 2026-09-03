using System.Collections.Concurrent;
using Read2Me.Core.Models;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Hands out the monotonic per-project revision numbers that order receipts against the snapshots
/// built from them. Revisions are process-local: they are not persisted, are not an
/// optimistic-concurrency column, and require no schema change (ADR 0007). A restarted process
/// starts again from 1, which is safe because a newly opened projection rebuilds from the database.
/// <para>
/// Only <see cref="BookMutations"/> calls <see cref="Next"/>, and only under that project's write
/// lock after its commit succeeded, so revision order is commit order.
/// </para>
/// </summary>
public sealed class BookRevisionSequence
{
    private readonly ConcurrentDictionary<string, long> _revisions = new(StringComparer.OrdinalIgnoreCase);

    public long Next(ProjectFolderId folderId) =>
        _revisions.AddOrUpdate(folderId.Value, 1, static (_, current) => current + 1);

    /// <summary>The last revision handed out for a project, or 0 if it has never been written.</summary>
    public long Current(ProjectFolderId folderId) =>
        _revisions.TryGetValue(folderId.Value, out var revision) ? revision : 0;
}
