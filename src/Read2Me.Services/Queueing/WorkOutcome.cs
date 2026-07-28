namespace Read2Me.Services.Queueing
{
    /// <summary>
    /// How one unit of queued work ended, reduced to what a retry decision needs — <b>provider
    /// behaviour only</b>. It says nothing about the <i>quality</i> of the work: an
    /// <see cref="Ok"/> attribution answer may still leave items unattributed, and whether a
    /// paragraph is finished is decided after apply, from the items, never from here.
    /// A queue's own richer outcome (<c>AttributionOutcome</c>, the audio pipeline's result)
    /// <i>carries</i> one rather than being replaced by it.
    /// <para>
    /// The Audio Queue emits three of the four members — it has no <see cref="Busy"/> — but the
    /// member exists so the shared disposition decision switches on one closed set.
    /// </para>
    /// <para>
    /// Not to be confused with <c>LlmRunOutcome</c>, which is one LLM call's ending and sits a
    /// layer below. Do not add quality states (<c>Unknown</c>, <c>ParseFailed</c>) here — a parse
    /// failure and a missing LLM config are both <see cref="Failed"/> as far as the queue is
    /// concerned.
    /// </para>
    /// </summary>
    /// <param name="Reason">Why the work ended this way, for logging and for the outcome marker.</param>
    public abstract record WorkOutcome(string? Reason)
    {
        /// <summary>The provider answered. Complete or settle — decided downstream, after apply.</summary>
        public sealed record Ok(string? Reason = null) : WorkOutcome(Reason);

        /// <summary>The work did not produce a usable result. Settle failed.</summary>
        public sealed record Failed(string? Reason) : WorkOutcome(Reason);

        /// <summary>Provider down; the watchdog is recovering it. Retry once.</summary>
        public sealed record Unavailable(string? Reason) : WorkOutcome(Reason);

        /// <summary>
        /// Provider alive but not ready — today only a llama endpoint still loading its model.
        /// Retry after backoff.
        /// </summary>
        public sealed record Busy(string? Reason) : WorkOutcome(Reason);
    }
}
