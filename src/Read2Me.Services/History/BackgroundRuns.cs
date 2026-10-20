using Read2Me.Core.Models;

namespace Read2Me.Services.History;

/// <summary>The work a <b>Background run</b> is made of: the Character Queue (attribution) or the voice batch.</summary>
public enum BackgroundRunKind { Attribution, VoiceBatch }

/// <summary>
/// Each project's open <b>Background run</b> (context/history.md) as a step scope: from the moment
/// the project goes idle → busy on the Character Queue or the voice batch until both have drained,
/// been cancelled or failed. The run tracker says when each kind starts and ends; run work commits
/// into the run through <see cref="HistoryStepScope.JoinBackgroundRuns"/>.
/// <para>
/// Like a request's scope, the step is created by the run's first state commit, so a run that
/// changed nothing leaves no step, and a commit from anyone else splits it into parts. Runs that
/// overlap share one step, labelled with both. Results the run commits record nothing, so they
/// never split it.
/// </para>
/// </summary>
public sealed class BackgroundRuns(ProjectHistory history)
{
    private readonly object _lock = new();
    private readonly Dictionary<ProjectFolderId, Run> _runs = [];

    /// <summary>
    /// <paramref name="kind"/> made <paramref name="folder"/> busy: opens the project's run, or
    /// joins the one already open.
    /// </summary>
    public void Started(ProjectFolderId folder, BackgroundRunKind kind)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(folder, out var run))
                _runs[folder] = run = new Run(new HistoryStepScope.OpenScope(folder, LabelOf([kind])));
            run.Busy.Add(kind);
            run.Joined.Add(kind);
            run.Scope.Label = LabelOf(run.Joined);
        }
    }

    /// <summary>
    /// <paramref name="kind"/> no longer keeps <paramref name="folder"/> busy. <paramref name="ending"/>
    /// says how it ended when that was early ("212 of 340 paragraphs (cancelled)"), or is null when
    /// it drained. Once no kind is busy the run closes its step, with every ending in its note.
    /// </summary>
    public Task EndedAsync(ProjectFolderId folder, BackgroundRunKind kind, string? ending = null)
    {
        Run? closing;
        lock (_lock)
        {
            if (!_runs.TryGetValue(folder, out var run) || !run.Busy.Remove(kind))
                return Task.CompletedTask;
            if (ending is not null)
                run.Endings.Add(ending);
            if (run.Busy.Count > 0)
                return Task.CompletedTask;
            _runs.Remove(folder);
            closing = run;
        }

        return history.CloseAsync(
            closing.Scope, closing.Endings.Count == 0 ? null : string.Join(StepNotes.Separator, closing.Endings));
    }

    /// <summary>The project's open run, as the scope its work commits into; null when none is open.</summary>
    internal HistoryStepScope.OpenScope? For(ProjectFolderId folder)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(folder)?.Scope;
    }

    /// <summary>"Attribution", "Voice batch", or "Attribution and voice batch" once both have joined.</summary>
    private static string LabelOf(IReadOnlyCollection<BackgroundRunKind> kinds) =>
        kinds.Contains(BackgroundRunKind.Attribution) && kinds.Contains(BackgroundRunKind.VoiceBatch)
            ? "Attribution and voice batch"
            : kinds.Contains(BackgroundRunKind.Attribution) ? "Attribution" : "Voice batch";

    private sealed class Run(HistoryStepScope.OpenScope scope)
    {
        public HistoryStepScope.OpenScope Scope { get; } = scope;
        public HashSet<BackgroundRunKind> Busy { get; } = [];
        public HashSet<BackgroundRunKind> Joined { get; } = [];
        public List<string> Endings { get; } = [];
    }
}
