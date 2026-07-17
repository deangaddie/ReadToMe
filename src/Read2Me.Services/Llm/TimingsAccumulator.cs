namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Ingests a single request's llama.cpp <see cref="LlmTimings"/> readings and answers rate
    /// questions about it. Constructed per request, fed per chunk, queried at the end.
    /// Replaces the deleted <c>StreamMetrics</c>: nothing here is estimated.
    /// </summary>
    /// <remarks>
    /// <b>Cumulative, not additive.</b> With <c>timings_per_token: true</c> every chunk restates
    /// the running totals so far, so <see cref="Add"/> <i>replaces</i> the latest reading and
    /// retains history for the window. It never sums — summing double-counts every token and
    /// produces a plausible-looking wrong number (ADR 0003).
    /// <para>
    /// <b>Pure.</b> It is fed arrival timestamps and never reads a clock, so it is testable with
    /// plain arrays and no fake clock. Do not reach for <c>DateTime.Now</c> in here.
    /// </para>
    /// <para>
    /// Rates recompute from <c>predicted_n ÷ predicted_ms</c> rather than reading the server's
    /// ready-made <c>predicted_per_second</c>, so a request rate and a run total are always
    /// arithmetically consistent. Absence is first-class throughout: a rate that cannot be
    /// computed is <c>null</c>, never <c>0</c>.
    /// </para>
    /// </remarks>
    public sealed class TimingsAccumulator
    {
        private readonly record struct Reading(TimeSpan Arrival, int PredictedN, double PredictedMs);

        /// <summary>Differenceable readings, in arrival order. Only fully-populated ones land here.</summary>
        private readonly List<Reading> _window = [];

        private LlmTimings? _latest;

        /// <summary>
        /// Records a chunk's cumulative reading, replacing the previous one. A null
        /// <paramref name="timings"/> (a chunk carrying no metrics) is ignored — it neither
        /// clears the latest reading nor contributes to the window.
        /// </summary>
        /// <param name="timings">The chunk's root-level timings, if it carried any.</param>
        /// <param name="arrival">
        /// When the chunk arrived, on any monotonic origin the caller likes. The accumulator only
        /// ever differences these, so the origin is irrelevant as long as it does not move.
        /// </param>
        public void Add(LlmTimings? timings, TimeSpan arrival)
        {
            if (timings is null)
                return;

            _latest = timings;

            if (timings is { PredictedN: { } n, PredictedMs: { } ms })
                _window.Add(new Reading(arrival, n, ms));
        }

        /// <summary>Tokens generated, per the latest reading's <c>predicted_n</c>.</summary>
        public int? TokensOut => _latest?.PredictedN;

        /// <summary>
        /// Server-measured generation time, per the latest reading's <c>predicted_ms</c>. Spans
        /// first token → stream end; model load and queue wait are excluded.
        /// </summary>
        public double? GenerationMs => _latest?.PredictedMs;

        /// <summary>
        /// Request Throughput: this request's rate in tokens/second, or null when either input is
        /// absent or the elapsed generation time is not positive. Never divides by zero, never
        /// reports <c>0</c> to mean "unknown".
        /// </summary>
        public double? Rate => ThroughputMath.Rate(TokensOut, GenerationMs);

        /// <summary>
        /// Generation Rate: the output rate in tokens/second over the sliding
        /// <paramref name="window"/> ending at the latest reading, obtained by differencing the
        /// cumulative readings at the window's two ends.
        /// </summary>
        /// <remarks>
        /// Before the window has filled it computes over a <b>partial</b> window — everything
        /// received so far, differenced against the start of generation — rather than returning
        /// null, so a request shorter than the window still shows a rate.
        /// </remarks>
        /// <returns>The windowed rate, or null when nothing differenceable has arrived.</returns>
        public double? WindowRate(TimeSpan window)
        {
            if (_window.Count == 0)
                return null;

            var latest = _window[^1];
            var cutoff = latest.Arrival - window;

            // The newest reading at or before the cutoff is the window's baseline. When there is
            // none the window is still partial, and generation's own origin (0 tokens, 0 ms) is
            // the honest baseline — that is what makes a short request report a real rate.
            var baselineN = 0;
            var baselineMs = 0.0;
            for (var i = _window.Count - 1; i >= 0; i--)
            {
                if (_window[i].Arrival > cutoff)
                    continue;
                baselineN = _window[i].PredictedN;
                baselineMs = _window[i].PredictedMs;
                break;
            }

            return ThroughputMath.Rate(latest.PredictedN - baselineN, latest.PredictedMs - baselineMs);
        }
    }
}
