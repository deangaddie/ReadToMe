using System;

namespace Read2Me.Services.Queueing
{
    /// <summary>
    /// What a queue does with an item once its unit of work has ended — the terminal value of the
    /// retry/settle policy, shared by both queues. Every member is <b>executable</b>: a queue module's
    /// <c>Apply</c> can run any of them, no arm throws. "Apply the work first, then decide" is not a
    /// transition, so it lives on <see cref="Plan"/> instead of here.
    /// <para>
    /// Five distinct cases rather than a settle-kind enum: each carries exactly its own data.
    /// <see cref="Failed"/> has no elapsed because it records none (it does not feed the rolling
    /// average); <see cref="Complete"/> has no reason because there is nothing to report.
    /// </para>
    /// </summary>
    public abstract record Disposition
    {
        /// <summary>
        /// The work is finished. <paramref name="Elapsed"/> null means "store, measure it yourself"
        /// from the last <c>MarkProcessing</c>; a queue whose own clock spans a drained batch passes
        /// its figure instead. <paramref name="Product"/> is the apply's own product where the
        /// executor needs it — audio's recorded path — and stays null on the character queue.
        /// </summary>
        public sealed record Complete(double? Elapsed, string? Product = null) : Disposition;

        /// <summary>
        /// The work ended without finishing: real work was done and it counts toward the rolling
        /// average, but a marker records that something is left. The shared name for what the
        /// character queue calls "unknown".
        /// </summary>
        public sealed record Unfinished(string? Reason, double? Elapsed) : Disposition;

        /// <summary>
        /// The work is abandoned. No elapsed: a failure did no measurable work and commonly returns
        /// through a re-queue, so counting it would double-bill the same item.
        /// </summary>
        public sealed record Failed(string? Reason) : Disposition;

        /// <summary>
        /// Put the item straight back on the queue, spending its one watchdog-recovery retry
        /// (<see cref="AttemptState.Retries"/>).
        /// </summary>
        public sealed record RetryOnce : Disposition;

        /// <summary>
        /// Put the item back on the queue after <paramref name="Delay"/>, spending one model-load
        /// retry (<see cref="AttemptState.Busies"/>). The delay is already computed by
        /// <see cref="QueueDisposition.Backoff"/> — the executor only waits it out.
        /// </summary>
        public sealed record RetryAfter(TimeSpan Delay) : Disposition;
    }

    /// <summary>
    /// The outcome of the shared, phase-1 decision: either a <see cref="Disposition"/> that can be
    /// executed as-is, or an instruction to apply the work first and let the queue's own phase-2
    /// decision read the apply's product.
    /// <para>
    /// The two phases cannot merge: applying-then-probing unconditionally would let a
    /// <see cref="Disposition.Failed"/> item whose work an <i>earlier</i> run had already applied
    /// probe clean and be recorded complete. The residual figure only carries meaning after a
    /// successful apply, so the decision that gates the apply comes first.
    /// </para>
    /// </summary>
    public abstract record Plan
    {
        /// <summary>Execute <paramref name="D"/>; nothing needs applying.</summary>
        public sealed record Now(Disposition D) : Plan;

        /// <summary>Apply the work, then ask the queue's own <c>DecideApplied</c>.</summary>
        public sealed record ApplyFirst : Plan;
    }
}
