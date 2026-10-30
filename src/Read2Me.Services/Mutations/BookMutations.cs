using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services.Checkpoints;
using Read2Me.Services.Events;
using Read2Me.Services.History;

namespace Read2Me.Services.Mutations;

/// <summary>
/// The one write-side entry point for changing a Book (ADR 0007). Every producer — Book View
/// gestures, the generic command endpoint, the queues, imports, AI edits — commits here, so the
/// rules that used to be rediscovered per caller have one home.
/// <para>
/// This module owns: per-project write serialization, the database transaction, the single commit
/// point, tracking-session eviction, monotonic revision allocation, receipt creation, and
/// best-effort publication <em>after</em> commit. A mutation implementation owns only the writes
/// themselves and the honest report of what it changed.
/// </para>
/// <para>
/// The write lock is held for the transaction and nothing else. It is not held across
/// reconciliation, across publication, or across any external artifact production (TTS, LLM,
/// ffmpeg) — those are staged by the producer before it calls here.
/// </para>
/// </summary>
public sealed class BookMutations(
    IServiceProvider serviceProvider,
    ProjectDbSession session,
    ProjectWriteLocks writeLocks,
    BookRevisionSequence revisions,
    IOptions<BookMutationOptions> options,
    EventBroadcaster<BookMutationReceipt> receipts,
    MutationOrigin origin,
    ProjectHistory history,
    HistoryStepScope stepScope,
    ProjectActivity activity,
    ILogger<BookMutations> logger)
{
    private BookMutationOptions Options => options.Value;

    public async Task<BookMutationOutcome> CommitAsync(BookMutation mutation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        // A mutation that names its origin keeps it; one that does not takes the scope's, so a
        // request-scoped caller (the API) is recognised on its receipt without every handler
        // between it and here knowing about origins.
        if (mutation.OriginId == Guid.Empty && origin.Id != Guid.Empty)
            mutation = mutation with { OriginId = origin.Id };

        // Resolved before the lock: an unregistered mutation is a wiring defect, not a conflict.
        var implementation = ResolveImplementation(mutation);

        if (ct.IsCancellationRequested)
            return Cancelled();
        if (RefusedWhileRestoring(mutation) is { } refused)
            return refused;

        IDisposable? writeLock;
        try
        {
            writeLock = await writeLocks.AcquireAsync(mutation.FolderId, Options.LockWaitBudget, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while queuing behind another writer. That is a cancelled gesture, not a
            // contended one, and the caller needs to be able to tell them apart.
            return Cancelled();
        }

        if (writeLock is null)
        {
            logger.LogWarning(
                "Book mutation {Mutation} for {Folder} gave up waiting {Budget} for the project write lock.",
                mutation.Name, mutation.FolderId.Value, Options.LockWaitBudget);
            return new BookMutationOutcome.Rejected(
                BookMutationRejection.Conflict,
                $"Another write to '{mutation.FolderId.Value}' is still in progress.");
        }

        BookMutationReceipt receipt;
        RecordedStep? step;
        try
        {
            // Again under the lock: a Restore may have begun while this waited for it.
            if (RefusedWhileRestoring(mutation) is { } refusedLate)
                return refusedLate;
            if (await ApplyInOneTransactionAsync(mutation, implementation, ct) is not { } committed)
                return new BookMutationOutcome.NoChange();

            // Allocated after the commit and still under the lock, so revision order is commit order.
            receipt = new BookMutationReceipt(
                mutation.FolderId, mutation.Name, Guid.NewGuid(), revisions.Next(mutation.FolderId), committed.Effects)
            {
                OriginId = mutation.OriginId,
            };
            step = committed.Step;
            // Under the lock, so the snapshot is the state after the step just committed.
            if (step is not null)
                await SnapshotIfDueAsync(mutation.FolderId);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (BookMutationRejectedException rejected)
        {
            return new BookMutationOutcome.Rejected(rejected.Reason, rejected.Message);
        }
        finally
        {
            // The handlers mutate through the session's long-lived tracking context, and some use
            // ExecuteDelete/ExecuteUpdate, which bypass the change tracker entirely. Without
            // eviction the next read returns entities from before the write.
            session.Evict(mutation.FolderId);
            writeLock.Dispose();
        }

        Publish(receipt);
        if (step is not null)
            Announce(mutation.FolderId, step);
        return new BookMutationOutcome.Committed(receipt);
    }

    /// <summary>
    /// Runs the implementation, saves, and commits — or rolls back. Returns the applied effects and
    /// the History step recorded with them, or null when the implementation changed nothing.
    /// <para>
    /// A state mutation is captured by History in the same transaction (ADR 0016), so a step and
    /// the change it records commit or roll back together. A results-only one opens no capture.
    /// </para>
    /// </summary>
    private async Task<CommittedChange?> ApplyInOneTransactionAsync(
        BookMutation mutation, ImplementationCall implementation, CancellationToken ct)
    {
        var db = await session.OpenAsync(mutation.FolderId);
        // Declared before the transaction so it is disposed after it: the capture attaches
        // history.db, which cannot be detached while a transaction is open.
        await using var capture = mutation.ChangesProjectState ? await history.BeginCaptureAsync(mutation.FolderId, db, ct) : null;
        var scope = stepScope.For(mutation.FolderId, out var runKind);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var effects = await implementation(mutation, db, ct);
        if (effects.ChangedNothing)
        {
            // Nothing to commit, so nothing to publish and no revision to consume. The rollback is
            // explicit because an implementation may have staged reads or work it then abandoned.
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        effects = await MarkCheckpointsStaleAsync(mutation, effects, db, ct);
        await db.SaveChangesAsync(ct);
        var step = capture is null || mutation is not ProjectStateMutation state ? null : await capture.RecordAsync(
            scope, runKind, state, effects, ct);

        // The last point at which cancellation is still honourable. Past it the change is real, so
        // the commit runs to completion regardless: a committed mutation must never be reported as
        // uncommitted.
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        if (scope is not null && step is not null)
        {
            // Only once the commit stands: a rolled-back chunk must not count toward the step.
            scope.StepId = step.StepId;
            scope.Part = step.Part;
            scope.Changes = step.Changes;
            scope.Kinds = step.Kinds;
        }
        return new CommittedChange(effects, step);
    }

    private sealed record CommittedChange(BookMutationEffects Effects, RecordedStep? Step);

    /// <summary>
    /// Past the commit, so best-effort: a snapshot that fails is only a slower open of later steps,
    /// and the next due commit takes one. A committed mutation is never reported as uncommitted.
    /// </summary>
    private async Task SnapshotIfDueAsync(ProjectFolderId folder)
    {
        try
        {
            await history.SnapshotIfDueAsync(folder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Snapshotting History of {Folder} after a commit failed; the commit stands.", folder.Value);
        }
    }

    /// <summary>Best-effort, like <see cref="Publish"/>: the step is committed whatever a subscriber does.</summary>
    private void Announce(ProjectFolderId folder, RecordedStep step)
    {
        try
        {
            history.Announce(folder, step);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Announcing History step {Step} on {Folder} failed; the step is committed.", step.StepId, folder.Value);
        }
    }

    /// <summary>
    /// Stales the confirmed checkpoints this mutation disturbed, inside its transaction, so a commit
    /// and the reconfirm it asks for land together. Reports <see cref="BookFacets.Checkpoints"/> only
    /// when a row actually changed: no row, or a row already stale, is nothing for the Overview to
    /// refetch.
    /// </summary>
    private static async Task<BookMutationEffects> MarkCheckpointsStaleAsync(
        BookMutation mutation, BookMutationEffects effects, ProjectDbContext db, CancellationToken ct)
    {
        var steps = CheckpointStaleness.StepsToStale(mutation, effects);
        if (steps.Count == 0)
            return effects;

        var staled = await db.StepCheckpoints
            .Where(c => steps.Contains(c.Step) && !c.Stale)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Stale, true), ct);
        return staled == 0 ? effects : effects with { Facets = effects.Facets | BookFacets.Checkpoints };
    }

    /// <summary>
    /// Best-effort, after the commit and after the lock is released. A subscriber that throws or
    /// blocks must never be able to fail — or slow — another producer's commit.
    /// <para>
    /// Publishing outside the lock means two receipts can reach a subscriber out of revision order.
    /// That is deliberate and safe: revisions exist precisely so a reader can reject a receipt older
    /// than the snapshot it already published, and holding the lock across publication would put
    /// every subscriber's work on another producer's commit path.
    /// </para>
    /// </summary>
    private void Publish(BookMutationReceipt receipt)
    {
        try
        {
            receipts.Publish(receipt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Publishing the receipt for {Mutation} on {Folder} failed; the mutation is committed.",
                receipt.MutationName, receipt.FolderId.Value);
        }
    }

    /// <summary>
    /// Every mutation but the Restore's own asks the gate. The gate refuses a write only while the
    /// project is restoring, so its refusal is always <see cref="BookMutationRejection.Restoring"/>.
    /// </summary>
    private BookMutationOutcome? RefusedWhileRestoring(BookMutation mutation) =>
        mutation is not RestoreStepMutation && activity.MayWrite(mutation.FolderId) is { } refusal
            ? new BookMutationOutcome.Rejected(BookMutationRejection.Restoring, refusal.Reason)
            : null;

    private BookMutationOutcome Cancelled() =>
        new BookMutationOutcome.Rejected(BookMutationRejection.Cancelled, "The mutation was cancelled before it committed.");

    private delegate Task<BookMutationEffects> ImplementationCall(
        BookMutation mutation, ProjectDbContext db, CancellationToken ct);

    // Dispatch by closed generic, the same shape BookCommandDispatcher uses for ICommandHandler<T>.
    // The two are not the same registry and must not be merged: a command is a wire name the API
    // contract owns, a mutation is a domain operation, and most mutations have no command at all.
    private ImplementationCall ResolveImplementation(BookMutation mutation)
    {
        var contract = typeof(IBookMutationImplementation<>).MakeGenericType(mutation.GetType());
        var implementation = serviceProvider.GetService(contract)
            ?? throw new NotSupportedException($"No mutation implementation registered for {mutation.Name}.");
        var apply = contract.GetMethod(nameof(IBookMutationImplementation<BookMutation>.ApplyAsync))!;
        return (m, db, ct) =>
        {
            try
            {
                return (Task<BookMutationEffects>)apply.Invoke(implementation, [m, db, ct])!;
            }
            catch (TargetInvocationException wrapped) when (wrapped.InnerException != null)
            {
                // An implementation that throws before its first await throws through the reflected
                // call itself. Unwrapping keeps a synchronous rejection an expected outcome, and a
                // synchronous defect the defect it actually is, rather than reflection plumbing.
                ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
                throw;
            }
        };
    }
}
