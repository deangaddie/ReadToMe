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

/// <summary>
/// How a producer that owns an external artifact reports a write that did not commit.
/// <para>
/// Both artifact adapters — the Audio Queue's take recorder and the Voice audio writer — face the
/// same question at the same point: the artifact is produced, the Book refused to name it, and the
/// caller above needs to know whether that was a failure or a cancellation. Cancellation keeps its
/// own exception type because callers act on it differently: a cancelled queue item is dropped, a
/// failed one is reported.
/// </para>
/// </summary>
public static class UncommittedArtifact
{
    /// <param name="what">What was being written, for the message — "item {id} audio", say.</param>
    public static Exception AsException(BookMutationOutcome outcome, string what, CancellationToken ct)
    {
        var reason = outcome switch
        {
            BookMutationOutcome.Rejected rejected => rejected.Message,
            // Reachable only for a mutation that can report no-change. An adapter that returned
            // success here would name an artifact it had just discarded.
            _ => "the Book recorded no change.",
        };

        return outcome is BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled }
            ? new OperationCanceledException(reason, ct)
            : new InvalidOperationException($"Writing {what} failed: {reason}");
    }
}
