using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.History;

/// <summary>
/// A <b>step scope</b> (spec, "Step boundaries"): everything committed for one project while it is
/// open is one History step, labelled with the scope owner's name in user terms. One API request is
/// one DI scope, so a state-changing endpoint opens one here and every mutation the request commits
/// — through whichever handlers and use cases — lands in the same step, whatever client sent it.
/// <para>
/// The step is created by the first commit that changes Project state, so a scope that commits
/// nothing leaves no step. Disposing the scope closes the step. A mutation committed with no scope
/// open is a step of its own, labelled from the mutation.
/// </para>
/// <para>
/// Whoever is interrupted is split: a commit from anyone else that lands while the scope's step is
/// open closes it, and the scope carries on in a new step, its next part.
/// </para>
/// <para>
/// Background run work — the queue worker's and the voice batch's DI scopes — calls
/// <see cref="JoinBackgroundRuns"/> instead, naming its kind, so what it commits lands in the
/// project's open Background run step (<see cref="BackgroundRuns"/>), and the step's label names
/// the kinds whose work landed in it.
/// </para>
/// <para>
/// Not the grouping key for echo suppression: <see cref="MutationOrigin"/> keeps that role.
/// </para>
/// </summary>
public sealed class HistoryStepScope(ProjectHistory history, BackgroundRuns runs)
{
    private readonly ProjectHistory _history = history;
    private OpenScope? _open;
    private BackgroundRunKind? _runKind;

    /// <summary>
    /// Opens a step scope for <paramref name="folder"/>. Inside a scope already open, this is a no-op:
    /// the outer owner — the request — keeps the step, so one request is at most one step.
    /// </summary>
    public IAsyncDisposable Open(ProjectFolderId folder, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (_open is not null)
            return NothingToClose.Instance;

        _open = new OpenScope(folder, label);
        return new Closer(this, _open);
    }

    /// <summary>
    /// Marks this DI scope as Background run work of <paramref name="kind"/>: what it commits to a
    /// project lands in that project's open run step, or is a step of its own when no run is open for it.
    /// </summary>
    public void JoinBackgroundRuns(BackgroundRunKind kind) => _runKind = kind;

    /// <summary>
    /// The scope open for <paramref name="folder"/>, or null when a commit to it is a step of its
    /// own. <paramref name="runKind"/> is the kind of Background run work the commit is, when it
    /// lands in the project's run.
    /// </summary>
    internal OpenScope? For(ProjectFolderId folder, out BackgroundRunKind? runKind)
    {
        runKind = null;
        if (_open is { } open && open.Folder == folder)
            return open;
        if (_runKind is not { } kind || runs.For(folder) is not { } run)
            return null;
        runKind = kind;
        return run;
    }

    /// <summary>
    /// The state of one open scope: its step and which part of the scope that step is, once a
    /// commit has created it, everything the step's commits changed so far, which its "what
    /// changed" describes, and the Background run work in it, which a run's label names. They are
    /// written only under the project's write lock, by the commit that moves them.
    /// </summary>
    internal sealed class OpenScope(ProjectFolderId folder, string label)
    {
        public ProjectFolderId Folder { get; } = folder;

        /// <summary>A request's label. A Background run's step is named for the run work in it instead.</summary>
        public string Label { get; } = label;

        public Guid? StepId { get; set; }
        public int Part { get; set; }
        public StepChanges Changes { get; set; } = StepChanges.None;

        /// <summary>The Background run kinds whose work the current step holds; none for a request's.</summary>
        public IReadOnlySet<BackgroundRunKind> Kinds { get; set; } = new HashSet<BackgroundRunKind>();
    }

    private sealed class Closer(HistoryStepScope owner, OpenScope scope) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            owner._open = null;
            await owner._history.CloseAsync(scope);
        }
    }

    private sealed class NothingToClose : IAsyncDisposable
    {
        public static readonly NothingToClose Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
