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
/// <see cref="JoinBackgroundRuns"/> instead, so what it commits lands in the project's open
/// Background run step (<see cref="BackgroundRuns"/>).
/// </para>
/// <para>
/// Not the grouping key for echo suppression: <see cref="MutationOrigin"/> keeps that role.
/// </para>
/// </summary>
public sealed class HistoryStepScope(ProjectHistory history, BackgroundRuns runs)
{
    private readonly ProjectHistory _history = history;
    private OpenScope? _open;
    private bool _backgroundRunWork;

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
    /// Marks this DI scope as Background run work: what it commits to a project lands in that
    /// project's open run step, or is a step of its own when no run is open for it.
    /// </summary>
    public void JoinBackgroundRuns() => _backgroundRunWork = true;

    /// <summary>The scope open for <paramref name="folder"/>, or null when a commit to it is a step of its own.</summary>
    internal OpenScope? For(ProjectFolderId folder) =>
        _open is { } open && open.Folder == folder ? open
        : _backgroundRunWork ? runs.For(folder)
        : null;

    /// <summary>
    /// The state of one open scope: its step and which part of the scope that step is, once a
    /// commit has created it, and everything the step's commits changed so far, which its "what
    /// changed" describes. The step, part and changes are written only under the project's write
    /// lock, by the commit that moves them; a Background run's label is renamed by
    /// <see cref="BackgroundRuns"/> whenever another run joins, and a commit takes it as it finds it.
    /// </summary>
    internal sealed class OpenScope(ProjectFolderId folder, string label)
    {
        public ProjectFolderId Folder { get; } = folder;

        /// <summary>A request's never changes; a Background run's names every run that has joined it.</summary>
        public string Label { get; set; } = label;

        public Guid? StepId { get; set; }
        public int Part { get; set; }
        public StepChanges Changes { get; set; } = StepChanges.None;
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
