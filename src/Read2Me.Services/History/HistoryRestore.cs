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
            // After the flag is set, so an enqueue that arrives from here on is refused, by the
            // endpoint or by the queue itself. One already past the queue's check can still land;
            // its writes are then refused with Restoring, and its items run again after the Restore.
            if (busy.For(folder) is { } b)
                return new RestoreOutcome.Refused(b.Reason);

            StepState target;
            switch (await history.OpenStepAsync(folder, stepId, ct))
            {
                case StepOpening.UnknownStep:
                    return new RestoreOutcome.UnknownStep();
                case StepOpening.CantOpen:
                    return new RestoreOutcome.CantOpen();
                case StepOpening.TryAgain:
                    return new RestoreOutcome.TryAgain();
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
            await using (stepScope.OpenRestore(folder, LabelOf(target.Step)))
                outcome = await mutations.CommitAsync(new RestoreStepMutation(folder, target.Step, work), ct);

            switch (outcome)
            {
                case BookMutationOutcome.Committed:
                    // The files first: whatever throws after them, the project is whole.
                    await WriteFilesAsync(folder);
                    landed = await history.HeadStepAsync(folder, CancellationToken.None);
                    dropped = editSessions.RemoveAll(folder);
                    return new RestoreOutcome.Restored(landed, work.AudioDiscardCount);
                case BookMutationOutcome.NoChange:
                    // The project already is the step's state: nothing to record, nothing dropped.
                    return new RestoreOutcome.Restored(null, 0);
                case BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled:
                    throw new OperationCanceledException(cancelled.Message, ct);
                case BookMutationOutcome.Rejected rejected:
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

    /// <summary>
    /// The state files, once the rows have committed: written from the Head's list while the
    /// restoring flag still holds every other write off. The Restore has happened whatever this
    /// does, so a failure is logged, and the repair at the next app start finishes what is left.
    /// </summary>
    private async Task WriteFilesAsync(ProjectFolderId folder)
    {
        try
        {
            if (!await history.WriteStateFilesAsync(folder, CancellationToken.None))
                logger.LogWarning("The Restore of {Folder} committed, but the write lock stayed taken, so its state files wait for the repair at the next app start.", folder.Value);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The Restore of {Folder} committed, but writing its state files failed; the repair at the next app start finishes them.", folder.Value);
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

    /// <summary>Opening the step failed for a reason that passes (an IO error, a full disk, a busy database).</summary>
    public sealed record TryAgain : RestoreOutcome;

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
