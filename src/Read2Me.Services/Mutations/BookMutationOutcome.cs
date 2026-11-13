using Read2Me.Services.History;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Why an uncommitted mutation did not commit. Every value is an expected condition. Callers
/// answer by its <see cref="BookMutationRefusalKind"/> (<see cref="BookMutationRefusal.Kind"/>);
/// the reason itself is for logs and the few callers that need the precise cause.
/// </summary>
public enum BookMutationRejection
{
    /// <summary>The request itself is not a legal operation (blank text, say).</summary>
    Validation,
    /// <summary>The request names something the Book does not contain.</summary>
    NotFound,
    /// <summary>
    /// Another writer holds the project, or what the request would write clashes with what the
    /// Book already holds (a Pronunciation's written form another entry has, say).
    /// </summary>
    Conflict,
    /// <summary>Cancellation was observed before the commit point.</summary>
    Cancelled,
    /// <summary>
    /// The Project activity gate refused the write: the project is restoring or being deleted
    /// (and, once the gate owns the start protocol, busy). The refusal's
    /// <see cref="BookMutationOutcome.Rejected.Activity"/> carries the gate's own kind and reason.
    /// A Restoring refusal is refused at once, without waiting for the write lock: the Restore is
    /// about to replace what the write was based on.
    /// </summary>
    Activity,
    /// <summary>
    /// The Book could take the write, but another book it is tied to could not (ADR 0018): a
    /// shared Voice that another linked book's rules still use, say. The state, not the request, is
    /// wrong.
    /// </summary>
    Blocked,
}

/// <summary>
/// How a caller treats a refused Book mutation, whatever refused it — the Refusal kind
/// (<c>context/book-structure.md</c>). Every adapter (HTTP, exceptions, use-case outcomes) answers
/// by this, never by the raw <see cref="BookMutationRejection"/>.
/// </summary>
public enum BookMutationRefusalKind
{
    /// <summary>The gesture was withdrawn before the commit point: never a failure, always rethrown as cancellation.</summary>
    Cancelled,
    /// <summary>The project's state, not the request, stops it now: another writer, the Project activity gate, a linked book.</summary>
    Contention,
    /// <summary>The request names something the Book does not contain.</summary>
    Missing,
    /// <summary>The request is not a legal operation.</summary>
    Invalid,
}

/// <summary>The one classification of a refused mutation, and the one cancellation step.</summary>
public static class BookMutationRefusal
{
    /// <summary>The only switch over <see cref="BookMutationRejection"/> values outside their producers.</summary>
    public static BookMutationRefusalKind Kind(this BookMutationRejection reason) => reason switch
    {
        BookMutationRejection.Cancelled => BookMutationRefusalKind.Cancelled,
        BookMutationRejection.Conflict or BookMutationRejection.Activity or BookMutationRejection.Blocked
            => BookMutationRefusalKind.Contention,
        BookMutationRejection.NotFound => BookMutationRefusalKind.Missing,
        BookMutationRejection.Validation => BookMutationRefusalKind.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unclassified mutation refusal."),
    };

    /// <summary>
    /// Rethrows a cancelled outcome as <see cref="OperationCanceledException"/> and otherwise
    /// returns it unchanged, so no adapter writes its own Cancelled arm. Nothing was written: a
    /// mutation stops observing cancellation at its commit point.
    /// </summary>
    public static T ThrowIfCancelled<T>(this T outcome, CancellationToken ct = default) where T : BookMutationOutcome =>
        outcome is BookMutationOutcome.Rejected { Kind: BookMutationRefusalKind.Cancelled } cancelled
            ? throw new OperationCanceledException(cancelled.Message, ct)
            : outcome;

    /// <summary>A refusal by the Project activity gate, carrying its kind and reason.</summary>
    public static BookMutationOutcome.Rejected FromActivity(ActivityRefusal refusal) =>
        new(BookMutationRejection.Activity, refusal.Reason) { Activity = refusal };
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
    public sealed record Rejected(BookMutationRejection Reason, string Message) : BookMutationOutcome
    {
        /// <summary>Problem-details extensions a refusal names its things with: a <see cref="BookMutationRejection.Blocked"/> one's <c>books</c>.</summary>
        public IReadOnlyDictionary<string, object?>? Extensions { get; init; }

        /// <summary>The Project activity gate's refusal, set exactly when <see cref="Reason"/> is <see cref="BookMutationRejection.Activity"/>.</summary>
        public ActivityRefusal? Activity { get; init; }

        /// <summary>How a caller treats this refusal (<see cref="BookMutationRefusal.Kind"/>).</summary>
        public BookMutationRefusalKind Kind => Reason.Kind();
    }
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
/// failed one is reported. So does a refusal while the project restores: a queue item refused that
/// way runs again after the Restore.
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

        return outcome switch
        {
            BookMutationOutcome.Rejected { Kind: BookMutationRefusalKind.Cancelled } => new OperationCanceledException(reason, ct),
            // Only a Restore is waited out: a delete never begins on a busy project, so no queue
            // item meets Deleting, and a Busy refusal is aimed at a user's write, not the queue's.
            BookMutationOutcome.Rejected { Activity.Kind: ActivityRefusalKind.Restoring } => new ProjectRestoringException($"Writing {what} failed: {reason}"),
            _ => new InvalidOperationException($"Writing {what} failed: {reason}"),
        };
    }
}

/// <summary>
/// A write refused because the project is restoring (an <see cref="BookMutationRejection.Activity"/> refusal of kind <see cref="ActivityRefusalKind.Restoring"/>). Still an
/// <see cref="InvalidOperationException"/>, so a caller that only reports failures needn't know it.
/// </summary>
public sealed class ProjectRestoringException(string message) : InvalidOperationException(message);
