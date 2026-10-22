using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.History;

/// <summary>
/// <b>Restore</b> (context/history.md): makes a step's Project state current by adding a new step,
/// "Restore to 'Split chapter' (14:02)", that records where it came from. Every later step stays in
/// History, so a Restore loses nothing and can itself be restored over.
/// <para>
/// Refused while anything is queued or running for the project, or while another Restore of it
/// runs. For its whole length the project's restoring flag is set, so every other write and enqueue
/// is refused at once. The change itself is one <see cref="RestoreStepMutation"/> committed under
/// its own step scope; its receipt is whole-project, so every open view rebuilds. Edit with AI
/// sessions planned against the project as it was are dropped. A <see cref="RestoreEvent"/> marks
/// the start and the finish.
/// </para>
/// </summary>
public sealed class HistoryRestore(
    ProjectHistory history,
    BookMutations mutations,
    HistoryStepScope stepScope,
    RestoringProjects restoring,
    IProjectBusyAnswer busy,
    IBookEditSessionStore editSessions,
    EventBroadcaster<RestoreEvent> events,
    TimeProvider clock,
    ILogger<HistoryRestore> logger)
{
    public async Task<RestoreOutcome> RestoreAsync(ProjectFolderId folder, Guid stepId, CancellationToken ct = default)
    {
        if (!restoring.TryBegin(folder))
            return new RestoreOutcome.Refused(RestoringProjects.Reason);

        var started = false;
        HistoryStep? landed = null;
        IReadOnlyList<string> dropped = [];
        try
        {
            // After the flag is set, so an enqueue request that arrives from here on is refused. One
            // already past the endpoint's check can still land; its queue writes are then refused
            // with Restoring, and those items fail rather than race the Restore.
            if (busy.For(folder) is { } b)
                return new RestoreOutcome.Refused(b.Reason);

            StepState target;
            switch (await history.OpenStepAsync(folder, stepId, ct))
            {
                case StepOpening.UnknownStep:
                    return new RestoreOutcome.UnknownStep();
                case StepOpening.CantOpen:
                    return new RestoreOutcome.CantOpen();
                case StepOpening.Opened opened:
                    target = opened.State;
                    break;
                default:
                    throw new InvalidOperationException("Unhandled step opening.");
            }

            started = true;
            Announce(new RestoreEvent(folder, RestoreEventKind.Started, Step: null, DroppedEditSessions: []));

            using var work = await history.PrepareRestoreAsync(folder, target, ct);
            BookMutationOutcome outcome;
            try
            {
                await using (stepScope.OpenRestore(folder, LabelOf(target.Step)))
                    outcome = await mutations.CommitAsync(new RestoreStepMutation(folder, target.Step, work), ct);
            }
            catch
            {
                // A throw says nothing about whether the rows committed, but a Restore that threw
                // is a defect either way; the files go back to what the project named before.
                work.PutFilesBack();
                throw;
            }

            switch (outcome)
            {
                case BookMutationOutcome.Committed:
                    landed = await history.HeadStepAsync(folder, CancellationToken.None);
                    dropped = editSessions.RemoveAll(folder);
                    return new RestoreOutcome.Restored(landed, work.AudioDiscardCount);
                case BookMutationOutcome.NoChange:
                    // The project already is the step's state: nothing to record, nothing dropped.
                    return new RestoreOutcome.Restored(null, 0);
                case BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled:
                    work.PutFilesBack();
                    throw new OperationCanceledException(cancelled.Message, ct);
                case BookMutationOutcome.Rejected rejected:
                    work.PutFilesBack();
                    return new RestoreOutcome.Refused(rejected.Message);
                default:
                    throw new InvalidOperationException($"Unhandled mutation outcome {outcome.GetType().Name}.");
            }
        }
        finally
        {
            restoring.End(folder);
            if (started)
                Announce(new RestoreEvent(folder, RestoreEventKind.Finished, landed, dropped));
        }
    }

    /// <summary>"Restore to 'Split chapter' (14:02)": the step brought back, and when it was, in local time.</summary>
    private string LabelOf(HistoryStep target)
    {
        var at = TimeZoneInfo.ConvertTime(target.EndedAt ?? target.StartedAt, clock.LocalTimeZone);
        return $"Restore to '{target.Label}' ({at:HH:mm})";
    }

    /// <summary>Best-effort: the Restore stands whatever a subscriber does.</summary>
    private void Announce(RestoreEvent e)
    {
        try
        {
            events.Publish(e);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Announcing a Restore of {Folder} failed; the Restore stands.", e.FolderId.Value);
        }
    }
}

/// <summary>What a Restore answered.</summary>
public abstract record RestoreOutcome
{
    private RestoreOutcome() { }

    /// <summary>
    /// The project is now the step's state. <paramref name="Step"/> is the Restore step it added, or
    /// null when the project already was that state and nothing changed; <paramref name="AudioDiscardCount"/>
    /// the items whose audio it discarded.
    /// </summary>
    public sealed record Restored(HistoryStep? Step, int AudioDiscardCount) : RestoreOutcome;

    /// <summary>The project's History has no step with that id.</summary>
    public sealed record UnknownStep : RestoreOutcome;

    /// <summary>The step can't be opened, so it can't be restored.</summary>
    public sealed record CantOpen : RestoreOutcome;

    /// <summary>Not now: the project is busy (the reason says with what), already restoring, or its write lock stayed taken.</summary>
    public sealed record Refused(string Reason) : RestoreOutcome;
}

public enum RestoreEventKind { Started, Finished }

/// <summary>
/// A Restore of a project started, or finished — with the step it added (null when it added none,
/// or failed) and the Edit with AI sessions it dropped, which their dialogs must say so of.
/// </summary>
public sealed record RestoreEvent(
    ProjectFolderId FolderId, RestoreEventKind Kind, HistoryStep? Step, IReadOnlyList<string> DroppedEditSessions);
