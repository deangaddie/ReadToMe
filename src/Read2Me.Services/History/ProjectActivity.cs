using System.Collections.Concurrent;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.History;

public enum ActivityRefusalKind { Busy, Restoring, Deleting }

/// <summary>Why the <see cref="ProjectActivity"/> gate refused an action; null from the gate means allowed.</summary>
public sealed record ActivityRefusal(ActivityRefusalKind Kind, string Reason);

/// <summary>
/// Opaque: a project and how many of its Restores had begun when <see cref="ProjectActivity.Mark"/>
/// took it, so <see cref="ProjectActivity.MayPlanEdit"/> can tell whether one began since.
/// </summary>
internal readonly record struct RestoreMark
{
    internal RestoreMark(ProjectFolderId folder, long begun) => (Folder, Begun) = (folder, begun);

    internal ProjectFolderId Folder { get; }
    internal long Begun { get; }
}

/// <summary>
/// The <b>Project activity</b> gate (spec #112, #346): owns a project's <b>busy</b>, <b>restoring</b>
/// and <b>deleting</b> states together and answers the questions callers ask, per the policy table.
/// Restoring wins over busy wherever the two overlap.
/// <list type="table">
/// <listheader><term>Action</term><description>idle / busy / restoring / deleting</description></listheader>
/// <item><term>Write</term><description>allowed / allowed / refused (the Restore's own mutation is exempt) / refused</description></item>
/// <item><term>Enqueue</term><description>allowed / allowed / refused / refused</description></item>
/// <item><term>View a step</term><description>allowed / refused, busy reason / refused / refused</description></item>
/// <item><term>Start a Restore</term><description>allowed / refused, busy reason / refused / refused</description></item>
/// <item><term>Start a delete</term><description>allowed / refused, busy reason / refused / refused</description></item>
/// <item><term>Plan Edit with AI</term><description>allowed / allowed / refused, also when a Restore began since the mark / refused</description></item>
/// </list>
/// An enqueue's restoring check must be atomic with its queue becoming busy, so a queue asks
/// <see cref="MayEnqueue(ProjectFolderId)"/> under its own lock.
/// <para>
/// Restoring and deleting are the project's <b>exclusive phase</b>, which the gate holds itself as
/// one claim per project: set for the whole of a Restore or a delete, so every other write and
/// enqueue is refused at once rather than waiting on the write lock. One claim per project means at
/// most one Restore runs at a time, and a Restore and a delete never overlap.
/// </para>
/// </summary>
public sealed class ProjectActivity
{
    public const string RestoringReason = "Project is restoring";
    public const string DeletingReason = "Project is being deleted";

    private readonly ConcurrentDictionary<string, Claim> _exclusive = new(StringComparer.OrdinalIgnoreCase);
    // Restores begun per project: a change since a mark means one began, even one that has ended since.
    private readonly ConcurrentDictionary<string, long> _begun = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IProjectBusyAnswer> _busy;

    /// <param name="busy">
    /// Resolved on each ask rather than injected directly: the busy answer is built from the queues,
    /// and the queues ask this gate before they enqueue, so taking it eagerly would be a DI loop.
    /// </param>
    public ProjectActivity(Func<IProjectBusyAnswer> busy) => _busy = busy;

    public ActivityRefusal? MayWrite(ProjectFolderId folder) => ExclusiveRefusal(folder);

    public ActivityRefusal? MayEnqueue(ProjectFolderId folder) => ExclusiveRefusal(folder);

    /// <summary>One enqueue across several projects: refused, as a whole, when any of them is.</summary>
    public ActivityRefusal? MayEnqueue(IEnumerable<ProjectFolderId> folders) =>
        folders.Distinct().Select(MayEnqueue).FirstOrDefault(r => r is not null);

    /// <summary>The step-preview GET and every <c>?at=</c> read.</summary>
    public ActivityRefusal? MayViewStep(ProjectFolderId folder) => ExclusiveRefusal(folder) ?? BusyRefusal(folder);

    /// <summary>
    /// Claims the restoring flag, then asks about busy, so an enqueue arriving after the claim is
    /// refused; when busy it releases the flag again. Null means the Restore began, and the caller
    /// must <see cref="EndRestore"/>.
    /// </summary>
    public ActivityRefusal? TryBeginRestore(ProjectFolderId folder)
    {
        if (TryClaim(folder, Restoring) is { } held)
            return held;
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

    public void EndRestore(ProjectFolderId folder) => Release(folder, Restoring);

    /// <summary>
    /// Claims deleting the way <see cref="TryBeginRestore"/> claims restoring: claim, then ask about
    /// busy, release when busy. It never cancels anything. Null means the delete began, and the
    /// caller must <see cref="EndDelete"/>.
    /// </summary>
    public ActivityRefusal? TryBeginDelete(ProjectFolderId folder)
    {
        if (TryClaim(folder, Deleting) is { } held)
            return held;
        if (BusyRefusal(folder) is { } busy)
        {
            EndDelete(folder);
            return busy;
        }
        return null;
    }

    public void EndDelete(ProjectFolderId folder) => Release(folder, Deleting);

    /// <summary>Completes when the project's Restore ends; already complete when none is running.</summary>
    public Task WhenRestoreEnded(ProjectFolderId folder) =>
        _exclusive.TryGetValue(folder.Value, out var held) && held.Refusal == Restoring
            ? held.Ended.Task
            : Task.CompletedTask;

    internal RestoreMark Mark(ProjectFolderId folder) => new(folder, RestoresBegun(folder));

    /// <summary>Refused while restoring or deleting, and when a Restore began since <paramref name="since"/>, even one that has ended.</summary>
    internal ActivityRefusal? MayPlanEdit(RestoreMark since) =>
        ExclusiveRefusal(since.Folder) ?? (RestoresBegun(since.Folder) != since.Begun ? Restoring : null);

    /// <summary>State for the History list's <c>restoring</c> field and the hub, not policy. Deleting is not restoring.</summary>
    public bool IsRestoring(ProjectFolderId folder) => ExclusiveRefusal(folder) == Restoring;

    private static readonly ActivityRefusal Restoring = new(ActivityRefusalKind.Restoring, RestoringReason);
    private static readonly ActivityRefusal Deleting = new(ActivityRefusalKind.Deleting, DeletingReason);

    /// <summary>A project's exclusive phase, named by the refusal it answers, and the signal for its end.</summary>
    private sealed class Claim(ActivityRefusal phase)
    {
        public ActivityRefusal Refusal { get; } = phase;
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Null when the claim is taken; otherwise the refusal of the phase already held.</summary>
    private ActivityRefusal? TryClaim(ProjectFolderId folder, ActivityRefusal phase)
    {
        // A claim released between the add and the look is gone: try the add again.
        while (true)
        {
            if (_exclusive.TryAdd(folder.Value, new Claim(phase)))
                return null;
            if (ExclusiveRefusal(folder) is { } held)
                return held;
        }
    }

    /// <summary>Ends the claim only when it is this phase's, so ending a Restore never ends a delete.</summary>
    private void Release(ProjectFolderId folder, ActivityRefusal phase)
    {
        if (_exclusive.TryGetValue(folder.Value, out var held) && held.Refusal == phase
            && _exclusive.TryRemove(new KeyValuePair<string, Claim>(folder.Value, held)))
            held.Ended.TrySetResult();
    }

    private long RestoresBegun(ProjectFolderId folder) => _begun.GetValueOrDefault(folder.Value);

    private ActivityRefusal? ExclusiveRefusal(ProjectFolderId folder) =>
        _exclusive.TryGetValue(folder.Value, out var held) ? held.Refusal : null;

    private ActivityRefusal? BusyRefusal(ProjectFolderId folder) =>
        _busy().For(folder) is { } busy ? new ActivityRefusal(ActivityRefusalKind.Busy, busy.Reason) : null;
}
