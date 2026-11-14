using System.Collections.Concurrent;
using System.Collections.Immutable;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.History;

public enum ActivityRefusalKind { Busy, Restoring, Deleting }

/// <summary>Why the <see cref="ProjectActivity"/> gate refused an action; null from the gate means allowed.</summary>
public sealed record ActivityRefusal(ActivityRefusalKind Kind, string Reason);

/// <summary>What a <see cref="ProjectActivity"/> start answered: refused, or started with the start's own result.</summary>
public abstract record ActivityStart<T>
{
    private ActivityStart() { }

    /// <summary>The gate allowed the start and ran it; <paramref name="Value"/> is what it returned.</summary>
    public sealed record Started(T Value) : ActivityStart<T>;

    /// <summary>The gate refused; the start never ran.</summary>
    public sealed record Refused(ActivityRefusal Refusal) : ActivityStart<T>;
}

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
/// The <b>Project activity</b> gate (spec #112, #346, #359): owns a project's <b>busy</b>,
/// <b>restoring</b> and <b>deleting</b> states together and answers the questions callers ask, per
/// the policy table. Restoring wins over busy wherever the two overlap.
/// <list type="table">
/// <listheader><term>Action</term><description>idle / busy / restoring / deleting</description></listheader>
/// <item><term>Write</term><description>allowed / allowed / refused (the Restore's own mutation is exempt) / refused</description></item>
/// <item><term>Start work (enqueue, or start a voice batch or assembly)</term><description>allowed / allowed / refused / refused</description></item>
/// <item><term>View a step</term><description>allowed / refused, busy reason / refused / refused</description></item>
/// <item><term>Start a Restore</term><description>allowed / refused, busy reason / refused / refused</description></item>
/// <item><term>Start a delete</term><description>allowed / refused, busy reason / refused / refused</description></item>
/// <item><term>Plan Edit with AI</term><description>allowed / allowed / refused, also when a Restore began since the mark / refused</description></item>
/// </list>
/// <para>
/// Restoring and deleting are the project's <b>exclusive phase</b>, which the gate holds itself as
/// one claim per project: set for the whole of a Restore or a delete, so every other write and
/// start is refused at once rather than waiting on the write lock. One claim per project means at
/// most one Restore runs at a time, and a Restore and a delete never overlap.
/// </para>
/// <para>
/// <b>The start protocol.</b> Work becomes busy only through <c>TryStart</c>, which checks and runs
/// the start under the gate's one lock. <see cref="TryBeginRestore"/> and <see cref="TryBeginDelete"/>
/// take the same lock around "claim, then ask the busy sources", so a Restore or a delete either
/// sees the started work as busy or the start sees it and is refused: neither can begin between the
/// other's check and its taking hold.
/// </para>
/// <para>
/// <b>Lock rules.</b> A start is quick and synchronous and never calls the gate. A busy source's
/// <see cref="IBusySource.BusyFor"/> may take the source's own lock but never calls the gate. So
/// the lock order is always gate, then source; a source keeps its own lock only for its own
/// consistency. The questions that claim nothing (<see cref="MayWrite"/>, <see cref="BusyFor"/>
/// and the rest) take no lock.
/// </para>
/// </summary>
public sealed class ProjectActivity
{
    public const string RestoringReason = "Project is restoring";
    public const string DeletingReason = "Project is being deleted";

    private readonly object _lock = new();
    // Changed only under _lock; read without it.
    private readonly ConcurrentDictionary<string, Claim> _exclusive = new(StringComparer.OrdinalIgnoreCase);
    // Restores begun per project: a change since a mark means one began, even one that has ended since.
    private readonly ConcurrentDictionary<string, long> _begun = new(StringComparer.OrdinalIgnoreCase);
    private ImmutableArray<IBusySource> _sources = [];

    /// <summary>
    /// Adds a source of busy work. Each source registers itself when it is built, so a source that
    /// was never built has never run anything and the gate needn't know it.
    /// </summary>
    public void Register(IBusySource source) =>
        ImmutableInterlocked.Update(ref _sources, sources => sources.Add(source));

    /// <summary>What the project is busy with, every registered source in <see cref="BusySourceKind"/> order; null when idle.</summary>
    public ProjectBusy? BusyFor(ProjectFolderId folder)
    {
        var busy = _sources.Select(s => s.BusyFor(folder)).OfType<BusySource>().OrderBy(s => s.Kind).ToArray();
        return busy.Length == 0 ? null : new ProjectBusy(busy);
    }

    public ActivityRefusal? MayWrite(ProjectFolderId folder) => ExclusiveRefusal(folder);

    /// <summary>
    /// Runs <paramref name="start"/> under the gate's lock unless the project is restoring or
    /// deleting. The start must leave its work busy before it returns, and must be quick,
    /// synchronous and never call the gate (see the class's lock rules).
    /// </summary>
    public ActivityStart<T> TryStart<T>(ProjectFolderId folder, Func<T> start) => TryStart([folder], start);

    /// <summary>One start across several projects: refused, as a whole, when any of them is.</summary>
    public ActivityStart<T> TryStart<T>(IEnumerable<ProjectFolderId> folders, Func<T> start)
    {
        lock (_lock)
        {
            if (folders.Distinct().Select(ExclusiveRefusal).FirstOrDefault(r => r is not null) is { } refusal)
                return new ActivityStart<T>.Refused(refusal);
            return new ActivityStart<T>.Started(start());
        }
    }

    /// <summary>A start with no result of its own, across one or more projects; null means it ran.</summary>
    public ActivityRefusal? TryStart(IEnumerable<ProjectFolderId> folders, Action start) =>
        TryStart(folders, () => { start(); return true; }) is ActivityStart<bool>.Refused refused ? refused.Refusal : null;

    /// <summary>The step-preview GET and every <c>?at=</c> read.</summary>
    public ActivityRefusal? MayViewStep(ProjectFolderId folder) => ExclusiveRefusal(folder) ?? BusyRefusal(folder);

    /// <summary>
    /// Claims the restoring flag, then asks about busy, so a start arriving after the claim is
    /// refused; when busy it releases the flag again. Null means the Restore began, and the caller
    /// must <see cref="EndRestore"/>.
    /// </summary>
    public ActivityRefusal? TryBeginRestore(ProjectFolderId folder)
    {
        lock (_lock)
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
    }

    public void EndRestore(ProjectFolderId folder) => Release(folder, Restoring);

    /// <summary>
    /// Claims deleting the way <see cref="TryBeginRestore"/> claims restoring: claim, then ask about
    /// busy, release when busy. It never cancels anything. Null means the delete began, and the
    /// caller must <see cref="EndDelete"/>.
    /// </summary>
    public ActivityRefusal? TryBeginDelete(ProjectFolderId folder)
    {
        lock (_lock)
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

    /// <summary>Under the lock: null when the claim is taken; otherwise the refusal of the phase already held.</summary>
    private ActivityRefusal? TryClaim(ProjectFolderId folder, ActivityRefusal phase) =>
        _exclusive.TryAdd(folder.Value, new Claim(phase)) ? null : ExclusiveRefusal(folder);

    /// <summary>Ends the claim only when it is this phase's, so ending a Restore never ends a delete.</summary>
    private void Release(ProjectFolderId folder, ActivityRefusal phase)
    {
        lock (_lock)
        {
            if (_exclusive.TryGetValue(folder.Value, out var held) && held.Refusal == phase
                && _exclusive.TryRemove(new KeyValuePair<string, Claim>(folder.Value, held)))
                held.Ended.TrySetResult();
        }
    }

    private long RestoresBegun(ProjectFolderId folder) => _begun.GetValueOrDefault(folder.Value);

    private ActivityRefusal? ExclusiveRefusal(ProjectFolderId folder) =>
        _exclusive.TryGetValue(folder.Value, out var held) ? held.Refusal : null;

    private ActivityRefusal? BusyRefusal(ProjectFolderId folder) =>
        BusyFor(folder) is { } busy ? new ActivityRefusal(ActivityRefusalKind.Busy, busy.Reason) : null;
}
