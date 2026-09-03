using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Read2Me.Data;
using Read2Me.Services.Events;

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
    BookMutationOptions options,
    EventBroadcaster<BookMutationReceipt> receipts,
    ILogger<BookMutations> logger)
{
    public async Task<BookMutationOutcome> CommitAsync(BookMutation mutation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        // Resolved before the lock: an unregistered mutation is a wiring defect, not a conflict.
        var implementation = ResolveImplementation(mutation);

        if (ct.IsCancellationRequested)
            return Cancelled();

        IDisposable? writeLock;
        try
        {
            writeLock = await writeLocks.AcquireAsync(mutation.FolderId, options.LockWaitBudget, ct);
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
                mutation.Name, mutation.FolderId.Value, options.LockWaitBudget);
            return new BookMutationOutcome.Rejected(
                BookMutationRejection.Conflict,
                $"Another write to '{mutation.FolderId.Value}' is still in progress.");
        }

        BookMutationReceipt receipt;
        try
        {
            var applied = await ApplyInOneTransactionAsync(mutation, implementation, ct);
            if (applied is null)
                return new BookMutationOutcome.NoChange();

            // Allocated after the commit and still under the lock, so revision order is commit order.
            receipt = new BookMutationReceipt(
                mutation.FolderId, mutation.Name, Guid.NewGuid(), revisions.Next(mutation.FolderId), applied);
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
        return new BookMutationOutcome.Committed(receipt);
    }

    /// <summary>
    /// Runs the implementation, saves, and commits — or rolls back. Returns the applied effects,
    /// or null when the implementation changed nothing.
    /// </summary>
    private async Task<BookMutationEffects?> ApplyInOneTransactionAsync(
        BookMutation mutation, ImplementationCall implementation, CancellationToken ct)
    {
        var db = await session.OpenAsync(mutation.FolderId);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var effects = await implementation(mutation, db, ct);
        if (effects.ChangedNothing)
        {
            // Nothing to commit, so nothing to publish and no revision to consume. The rollback is
            // explicit because an implementation may have staged reads or work it then abandoned.
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        await db.SaveChangesAsync(ct);

        // The last point at which cancellation is still honourable. Past it the change is real, so
        // the commit runs to completion regardless: a committed mutation must never be reported as
        // uncommitted.
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return effects;
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

    private BookMutationOutcome Cancelled() =>
        new BookMutationOutcome.Rejected(BookMutationRejection.Cancelled, "The mutation was cancelled before it committed.");

    private delegate Task<BookMutationEffects> ImplementationCall(
        BookMutation mutation, ProjectDbContext db, CancellationToken ct);

    // Dispatch by closed generic, the same shape BookCommandHandler uses for ICommandHandler<T>.
    // The duplication is migration scaffolding: the contraction ticket deletes that dispatcher — and
    // its own eviction finally-block — once no caller holds the legacy façade.
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
