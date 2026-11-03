using Read2Me.Services.History;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Work that belongs to a Book commit but lies outside the book it commits in: the series Voice
/// copy (ADR 0018) refuses a write another linked book can't take, and carries a committed Voice
/// edit into every other linked book. <see cref="BookMutations"/> asks each registered hook before
/// it takes the write lock, and runs what the hook planned once the commit stands, in the same
/// call, so the producer's request waits for it.
/// <para>
/// Resolved from the scope at commit time, not injected: a hook writes through
/// <see cref="BookMutations"/> itself, and a constructor dependency would be a cycle.
/// </para>
/// </summary>
public interface IBookCommitHook
{
    /// <summary>
    /// Read before the commit: a refusal, which stops the commit, or what to do after it, or
    /// <see cref="BookCommitPlan.None"/>. Must not hold anything a commit waits for.
    /// </summary>
    Task<BookCommitPlan> PlanAsync(BookMutation mutation, CancellationToken ct);
}

/// <summary>A hook's answer before the commit: refuse it, follow it up, or neither.</summary>
public sealed record BookCommitPlan(
    BookMutationOutcome.Rejected? Refusal,
    Func<CommittedBookChange, Task>? AfterCommit)
{
    public static readonly BookCommitPlan None = new(null, null);

    public static BookCommitPlan Refuse(BookMutationOutcome.Rejected refusal) => new(refusal, null);

    public static BookCommitPlan After(Func<CommittedBookChange, Task> afterCommit) => new(null, afterCommit);
}

/// <summary>
/// A commit that stands: its receipt, and the History step it landed in (label and action), or
/// null for a results-only commit.
/// </summary>
public sealed record CommittedBookChange(BookMutationReceipt Receipt, string? StepLabel, StepAction? StepAction);
