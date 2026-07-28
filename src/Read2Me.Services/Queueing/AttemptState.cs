namespace Read2Me.Services.Queueing
{
    /// <summary>
    /// The retry budgets a queued item carries, as one value. The two counts are <b>independent
    /// and semantically distinct</b>: <paramref name="Retries"/> is once-only (spent on an
    /// unavailable provider while the watchdog recovers — a second outage for the same item
    /// fails it), <paramref name="Busies"/> is unbounded with backoff (spent while a model loads,
    /// where failing would evict the very load being waited on).
    /// <para>
    /// It rides the work payload rather than a keyed store, so its lifetime <i>is</i> the
    /// message's: a fresh enqueue starts at <c>default</c> — exactly the reset a user's re-queue
    /// wants, with nothing to remember to clear — and <c>CancelAll</c> drops in-flight budgets
    /// with the channel. A keyed store would need an eviction rule on every terminal arm or leak
    /// a budget into the next run of the same item.
    /// </para>
    /// </summary>
    /// <param name="Retries">Watchdog-recovery retries already spent. Budget: one.</param>
    /// <param name="Busies">Model-load retries already spent. No budget; each one backs off further.</param>
    public readonly record struct AttemptState(int Retries = 0, int Busies = 0)
    {
        /// <summary>Spends one watchdog-recovery retry.</summary>
        public AttemptState WithRetry() => this with { Retries = Retries + 1 };

        /// <summary>Spends one model-load retry. Never touches <see cref="Retries"/>.</summary>
        public AttemptState WithBusy() => this with { Busies = Busies + 1 };
    }
}
