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
/// Not the grouping key for echo suppression: <see cref="MutationOrigin"/> keeps that role.
/// </para>
/// </summary>
public sealed class HistoryStepScope(ProjectHistory history)
{
    private readonly ProjectHistory _history = history;
    private OpenScope? _open;

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

    /// <summary>The scope open for <paramref name="folder"/>, or null when a commit to it is a step of its own.</summary>
    internal OpenScope? For(ProjectFolderId folder) => _open is { } open && open.Folder == folder ? open : null;

    /// <summary>
    /// The state of one open scope: its step, once a commit has created it, and everything its
    /// commits changed so far, which the step's "what changed" describes.
    /// </summary>
    internal sealed class OpenScope(ProjectFolderId folder, string label)
    {
        public ProjectFolderId Folder { get; } = folder;
        public string Label { get; } = label;
        public Guid? StepId { get; set; }
        public StepChanges Changes { get; set; } = StepChanges.None;
    }

    private sealed class Closer(HistoryStepScope owner, OpenScope scope) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            owner._open = null;
            if (scope.StepId is { } stepId)
                await owner._history.CloseAsync(scope.Folder, stepId);
        }
    }

    private sealed class NothingToClose : IAsyncDisposable
    {
        public static readonly NothingToClose Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
