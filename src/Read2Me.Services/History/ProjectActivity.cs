using System.Collections.Concurrent;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.History;

public enum ActivityRefusalKind { Busy, Restoring }

/// <summary>Why the <see cref="ProjectActivity"/> gate refused an action; null from the gate means allowed.</summary>
public sealed record ActivityRefusal(ActivityRefusalKind Kind, string Reason);

/// <summary>
/// Opaque: a project and how many of its Restores had begun when <see cref="ProjectActivity.Mark"/>
/// took it, so <see cref="ProjectActivity.MayPlanEdit"/> can tell whether one began since.
/// </summary>
public readonly record struct RestoreMark
{
    internal RestoreMark(ProjectFolderId folder, long begun) => (Folder, Begun) = (folder, begun);

    internal ProjectFolderId Folder { get; }
    internal long Begun { get; }
}

/// <summary>
/// The <b>Project activity</b> gate (spec #112): owns a project's <b>busy</b> and <b>restoring</b>
/// states together and answers the questions callers ask, per the policy table. Restoring wins
/// wherever the two overlap.
/// <list type="table">
/// <listheader><term>Action</term><description>idle / busy / restoring</description></listheader>
/// <item><term>Write</term><description>allowed / allowed / refused (the Restore's own mutation is exempt)</description></item>
/// <item><term>Enqueue</term><description>allowed / allowed / refused</description></item>
/// <item><term>View a step</term><description>allowed / refused, busy reason / refused</description></item>
/// <item><term>Start a Restore</term><description>allowed / refused, busy reason / refused</description></item>
/// <item><term>Plan Edit with AI</term><description>allowed / allowed / refused, also when a Restore began since the mark</description></item>
/// </list>
/// An enqueue's restoring check must be atomic with its queue becoming busy, so a queue asks
/// <see cref="MayEnqueue"/> under its own lock.
/// <para>
/// The gate holds the per-project <b>restoring flag</b> itself: set for the whole of a Restore, so
/// every other write and enqueue is refused at once rather than waiting on the write lock. At most
/// one Restore runs per project.
/// </para>
/// </summary>
public sealed class ProjectActivity
{
    public const string RestoringReason = "Project is restoring";

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _restoring = new(StringComparer.OrdinalIgnoreCase);
    // Restores begun per project: a change since a mark means one began, even one that has ended since.
    private readonly ConcurrentDictionary<string, long> _begun = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IProjectBusyAnswer> _busy;

    /// <param name="busy">
    /// Resolved on each ask rather than injected directly: the busy answer is built from the queues,
    /// and the queues ask this gate before they enqueue, so taking it eagerly would be a DI loop.
    /// </param>
    public ProjectActivity(Func<IProjectBusyAnswer> busy) => _busy = busy;

    public ActivityRefusal? MayWrite(ProjectFolderId folder) => RestoringRefusal(folder);

    public ActivityRefusal? MayEnqueue(ProjectFolderId folder) => RestoringRefusal(folder);

    /// <summary>The step-preview GET and every <c>?at=</c> read.</summary>
    public ActivityRefusal? MayViewStep(ProjectFolderId folder) => RestoringRefusal(folder) ?? BusyRefusal(folder);

    /// <summary>
    /// Claims the restoring flag, then asks about busy, so an enqueue arriving after the claim is
    /// refused; when busy it releases the flag again. Null means the Restore began, and the caller
    /// must <see cref="EndRestore"/>.
    /// </summary>
    public ActivityRefusal? TryBeginRestore(ProjectFolderId folder)
    {
        if (!_restoring.TryAdd(folder.Value, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))
            return Restoring;
        if (BusyRefusal(folder) is { } busy)
        {
            EndRestore(folder);
            return busy;
        }
        // Counted only once it has begun: a plan marked during the claim is refused by the flag
        // while it is up, and by the count after.
        _begun.AddOrUpdate(folder.Value, 1, (_, begun) => begun + 1);
        return null;
    }

    public void EndRestore(ProjectFolderId folder)
    {
        if (_restoring.TryRemove(folder.Value, out var ended))
            ended.TrySetResult();
    }

    /// <summary>Completes when the project's Restore ends; already complete when none is running.</summary>
    public Task WhenRestoreEnded(ProjectFolderId folder) =>
        _restoring.TryGetValue(folder.Value, out var running) ? running.Task : Task.CompletedTask;

    public RestoreMark Mark(ProjectFolderId folder) => new(folder, RestoresBegun(folder));

    /// <summary>Refused while restoring, and when a Restore began since <paramref name="since"/>, even one that has ended.</summary>
    public ActivityRefusal? MayPlanEdit(RestoreMark since) =>
        RestoringRefusal(since.Folder) ?? (RestoresBegun(since.Folder) != since.Begun ? Restoring : null);

    /// <summary>State for the History list's <c>restoring</c> field and the hub, not policy.</summary>
    public bool IsRestoring(ProjectFolderId folder) => _restoring.ContainsKey(folder.Value);

    private static readonly ActivityRefusal Restoring = new(ActivityRefusalKind.Restoring, RestoringReason);

    private long RestoresBegun(ProjectFolderId folder) => _begun.GetValueOrDefault(folder.Value);

    private ActivityRefusal? RestoringRefusal(ProjectFolderId folder) => IsRestoring(folder) ? Restoring : null;

    private ActivityRefusal? BusyRefusal(ProjectFolderId folder) =>
        _busy().For(folder) is { } busy ? new ActivityRefusal(ActivityRefusalKind.Busy, busy.Reason) : null;
}
