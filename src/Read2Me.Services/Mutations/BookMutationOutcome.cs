namespace Read2Me.Services.Mutations;

/// <summary>Why an uncommitted mutation did not commit. Every value is an expected condition.</summary>
public enum BookMutationRejection
{
    /// <summary>The request itself is not a legal operation (blank text, say).</summary>
    Validation,
    /// <summary>The request names something the Book does not contain.</summary>
    NotFound,
    /// <summary>Another writer holds the project, or the database refused the write.</summary>
    Conflict,
    /// <summary>The caller's view of the Book is too old to base this mutation on.</summary>
    Stale,
    /// <summary>Cancellation was observed before the commit point.</summary>
    Cancelled,
}

/// <summary>
/// What <see cref="BookMutations.CommitAsync"/> did. The three cases are exhaustive and
/// deliberately distinct: only <see cref="Committed"/> changed the Book, only it consumed a
/// revision, and only it published a receipt. Unexpected implementation defects are not outcomes —
/// they throw.
/// </summary>
public abstract record BookMutationOutcome
{
    private BookMutationOutcome() { }

    /// <summary>The mutation committed. The receipt describes what it actually changed.</summary>
    public sealed record Committed(BookMutationReceipt Receipt) : BookMutationOutcome;

    /// <summary>A valid operation that changed nothing: nothing committed, no revision, no receipt.</summary>
    public sealed record NoChange : BookMutationOutcome;

    /// <summary>An expected failure. Nothing committed.</summary>
    public sealed record Rejected(BookMutationRejection Reason, string Message) : BookMutationOutcome;
}

/// <summary>
/// Thrown by a mutation implementation to report an expected uncommitted outcome.
/// <see cref="BookMutations"/> rolls the transaction back and returns
/// <see cref="BookMutationOutcome.Rejected"/>; any other exception is a defect and propagates.
/// </summary>
public sealed class BookMutationRejectedException(BookMutationRejection reason, string message)
    : Exception(message)
{
    public BookMutationRejection Reason { get; } = reason;
}
