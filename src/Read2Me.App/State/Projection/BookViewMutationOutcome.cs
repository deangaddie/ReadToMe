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

        /// <summary>A valid gesture that changed nothing. Nothing committed, nothing republished.</summary>
        public sealed record NoChange : BookViewMutationOutcome;

        /// <summary>An expected refusal. Nothing committed, and the Book View is untouched.</summary>
        public sealed record Uncommitted(BookMutationRejection Reason, string Message)
            : BookViewMutationOutcome;

        public bool Committed => this is Coherent;
    }
}
