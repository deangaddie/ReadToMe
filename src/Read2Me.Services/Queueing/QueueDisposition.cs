using System;

namespace Read2Me.Services.Queueing
{
    /// <summary>
    /// The shared retry/settle policy, as a pure function. Phase 1 of the decision: it reads only
    /// provider behaviour (<see cref="WorkOutcome"/>), whether there is anything to apply, and the
    /// item's own retry budgets — never a store, a clock or a database. Whether an applied answer
    /// actually <i>finished</i> the item is phase 2, per queue, after the apply.
    /// <para>
    /// This module takes <b>no dependencies</b>. If something needs injecting here, the two phases
    /// have been mixed up.
    /// </para>
    /// </summary>
    public static class QueueDisposition
    {
        /// <summary>
        /// Decides what happens to an item whose work has ended.
        /// </summary>
        /// <param name="outcome">How the provider behaved.</param>
        /// <param name="hasApplicableWork">
        /// Whether the answer carries work worth applying. An <see cref="WorkOutcome.Ok"/> with
        /// nothing to apply — the character queue's empty paragraph — settles
        /// <see cref="Disposition.Unfinished"/> and must never reach the apply.
        /// </param>
        /// <param name="attempts">
        /// The budgets already spent. Each arm reads exactly the counter its own retry disposition
        /// writes: <see cref="WorkOutcome.Unavailable"/> reads <see cref="AttemptState.Retries"/>
        /// and yields <see cref="Disposition.RetryOnce"/>; <see cref="WorkOutcome.Busy"/> reads
        /// <see cref="AttemptState.Busies"/> and yields <see cref="Disposition.RetryAfter"/>. That
        /// is what makes the two budgets independent structurally rather than by comment.
        /// </param>
        public static Plan Decide(WorkOutcome outcome, bool hasApplicableWork, AttemptState attempts) =>
            outcome switch
            {
                // An answer applies whether or not every part of it was resolved: what it *did*
                // resolve is real work, and "is it finished?" is settled after the apply.
                WorkOutcome.Ok when hasApplicableWork => new Plan.ApplyFirst(),

                WorkOutcome.Ok ok => new Plan.Now(new Disposition.Unfinished(ok.Reason, null)),

                WorkOutcome.Failed failed => new Plan.Now(new Disposition.Failed(failed.Reason)),

                // The watchdog is recovering the provider. Retry once so recovery is invisible in
                // the results; a second outage for the same item means the provider is down.
                WorkOutcome.Unavailable unavailable => new Plan.Now(
                    attempts.Retries > 0
                        ? new Disposition.Failed(unavailable.Reason)
                        : new Disposition.RetryOnce()),

                // Provider busy, not dead: retry indefinitely with a growing backoff. Failing or
                // escalating would evict the very model load being waited on, so this path never
                // spends — or even reads — the once-only Retries budget.
                WorkOutcome.Busy => new Plan.Now(new Disposition.RetryAfter(Backoff(attempts.Busies))),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(outcome), outcome, "Unhandled WorkOutcome — the decision must stay total."),
            };

        /// <summary>
        /// Exponential-with-cap backoff for an indefinitely-retried busy provider.
        /// <paramref name="attempt"/> is the 0-based count of retries already spent:
        /// <b>0→2s, 1→4s, 2→8s, 3→16s, 4→30s (cap)</b>, and 30s thereafter. The doubling factor is
        /// bounded before it can overflow, so a long-running wedged load simply polls at the cap.
        /// <para>
        /// This is the <i>only</i> statement of these numbers in prose; the theory table in
        /// <c>QueueDispositionTests</c> restates them executably, so it cannot drift.
        /// One curve is shared by both queues — a queue that genuinely needs a different one is a
        /// reason to parameterise this then, not now.
        /// </para>
        /// </summary>
        public static TimeSpan Backoff(int attempt)
        {
            if (attempt < 0) attempt = 0;
            var factor = attempt >= 4 ? 16 : 1 << attempt;
            var delay = BackoffBase * factor;
            return delay < BackoffCap ? delay : BackoffCap;
        }

        private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(30);
    }
}
