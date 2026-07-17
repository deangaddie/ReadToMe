namespace Read2Me.Services.Llm
{
    /// <summary>
    /// One chunk's server-measured reading, published by the producer as it arrives so the
    /// throughput aggregator can fill its ring without ever reading a clock.
    /// </summary>
    /// <remarks>
    /// <b>This rides its own <c>EventBroadcaster</c>, deliberately not the <see cref="LlmStreamEvent"/>
    /// family.</b> With <c>timings_per_token: true</c> one of these arrives per token, and every
    /// <see cref="LlmStreamEvent"/> subscriber is built for the opposite: <c>LlmStreamView</c>
    /// repaints on <i>every</i> event it receives, unfiltered, and <c>EventJournal</c> buffers
    /// every event of the turn to replay to late subscribers. Joining that family would have cost
    /// a second SignalR repaint per token on every open circuit and buffered a reading per token
    /// for replay — the exact per-token amplification the pull seam exists to make structurally
    /// impossible (ADR 0003, decision 06). A separate family costs one delegate invoke per token
    /// and is what <c>EventBroadcaster</c>'s "one transport, many event families" is for.
    /// <para>
    /// <b>Values are cumulative running totals within one request</b>, restated by every chunk —
    /// see <see cref="LlmTimings"/>. A consumer differences consecutive readings; it never sums
    /// them. The counters restart at the next request, so a consumer must rebaseline on
    /// <see cref="RequestStarted"/> rather than differencing across a request boundary.
    /// </para>
    /// </remarks>
    /// <param name="Timings">The chunk's root-level timings. Only chunks that carry them are published.</param>
    /// <param name="Arrival">
    /// When the chunk arrived, on a <b>process-wide monotonic origin</b> supplied by the producer.
    /// The origin is irrelevant — consumers only ever difference these — but it must not move, and
    /// it must not restart per request, or a run's buckets would collide across its requests.
    /// The stamp comes from the producer precisely so that consumers stay clock-free.
    /// </param>
    public sealed record LlmTimingsSample(LlmTimings Timings, TimeSpan Arrival);
}
