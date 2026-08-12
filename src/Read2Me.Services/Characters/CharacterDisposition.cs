using Read2Me.Services.Queueing;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// The character queue's phase-2 decision: given that an attribution answer was applied, is the
    /// paragraph finished? Pure — the caller does the probing and passes the figure in.
    /// </summary>
    public static class CharacterDisposition
    {
        /// <summary>
        /// Decides a paragraph's fate from the apply's own product: the number of Character items
        /// still carrying no character. Any residue leaves the paragraph unfinished, whatever the
        /// LLM's own confidence was — and conversely an "unknown" answer that left nothing
        /// unattributed completes, because the items it named were already stamped.
        /// </summary>
        /// <param name="unattributed">Character items still unstamped after the apply.</param>
        /// <param name="elapsed">
        /// The queue's own figure — one stopwatch spans a drained batch here, so the store must not
        /// measure it from <c>MarkProcessing</c>.
        /// </param>
        /// <param name="reason">Why the answer fell short, for the outcome marker.</param>
        public static Disposition DecideApplied(int unattributed, double elapsed, string? reason) =>
            unattributed > 0
                ? new Disposition.Unfinished(reason, elapsed)
                : new Disposition.Complete(elapsed);
    }
}
