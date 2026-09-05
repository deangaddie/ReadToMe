using Read2Me.Services.Mutations;

namespace Read2Me.App.State.Projection
{
    /// <summary>
    /// What <see cref="BookViewProjection.MutateAsync"/> did — a write-side outcome and this
    /// circuit's reconciliation of it, answered as one thing (ADR 0007).
    /// <para>
    /// The distinction the presenter needs is between "the Book changed" and "the Book did not",
    /// because only the second is worth telling the reader about: a coherent success needs no
    /// announcement, since the Book View in front of them already shows it.
    /// </para>
    /// </summary>
    public abstract record BookViewMutationOutcome
    {
        private BookViewMutationOutcome() { }

        /// <summary>
        /// The mutation committed and this circuit's Book View has been rebuilt from it. The
        /// snapshot is the one already published, so the gesture is finished, not merely accepted.
        /// </summary>
        public sealed record Coherent(BookMutationReceipt Receipt, BookViewSnapshot Snapshot)
            : BookViewMutationOutcome;

        /// <summary>
        /// The mutation committed, but this circuit's Book View could not be brought up to it: the
        /// targeted refresh and the rebuild behind it both failed, or the reader moved off the Book
        /// while it was committing.
        /// <para>
        /// Deliberately not an <see cref="Uncommitted"/>: the Book <em>changed</em>, so retrying the
        /// gesture would apply it twice, and a producer holding an external artifact must keep it.
        /// What is on screen is the last coherent snapshot, and the projection stays stale until
        /// <see cref="BookViewProjection.RetryRebuildAsync"/> succeeds.
        /// </para>
        /// </summary>
        /// <param name="Snapshot">
        /// The last coherent snapshot, marked stale — or null when the reader moved to another
        /// project while the change was committing, which is the one case where this outcome does
        /// <em>not</em> leave a Stale Book View projection behind: what is on screen is a coherent
        /// view of a different Book, so there is nothing to retry, only something to report.
        /// </param>
        public sealed record CommittedButStale(BookMutationReceipt Receipt, BookViewSnapshot? Snapshot)
            : BookViewMutationOutcome;

        /// <summary>A valid gesture that changed nothing. Nothing committed, nothing republished.</summary>
        public sealed record NoChange : BookViewMutationOutcome;

        /// <summary>An expected refusal. Nothing committed, and the Book View is untouched.</summary>
        public sealed record Uncommitted(BookMutationRejection Reason, string Message)
            : BookViewMutationOutcome;

        public bool Committed => this is Coherent or CommittedButStale;
    }
}
