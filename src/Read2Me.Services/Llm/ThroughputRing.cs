namespace Read2Me.Services.Llm
{
    /// <summary>
    /// The sparkline's history: a ring of <see cref="ThroughputSnapshot.HistoryBuckets"/> ×
    /// <see cref="ThroughputSnapshot.HistoryBucketMs"/>ms buckets, each holding the tokens and the
    /// server-measured milliseconds that landed in it.
    /// </summary>
    /// <remarks>
    /// <b>Clock-free, and that is the whole point.</b> The ring advances only when a reading is
    /// fed to it, and a bucket's identity is derived from that reading's producer-supplied arrival
    /// stamp. Nothing here reads <c>DateTime.Now</c> or ticks on a timer, so the same event
    /// sequence yields the same ring no matter when it is read — two tabs opened a second apart
    /// chart a run identically (decision 06).
    /// <para>
    /// <b>An empty bucket is absence, not zero.</b> A bucket nothing arrived in reports null; a
    /// bucket reports <c>0</c> only if the server genuinely measured no tokens over real elapsed
    /// time. A gap in the chart means "nothing was generating", which is exactly the stutter the
    /// sparkline exists to show — collapsing it to <c>0</c> would make it indistinguishable from
    /// a measured stall (ADR 0003).
    /// </para>
    /// <para>
    /// History lives here rather than in a component so the 10s span is a domain fact. Left in the
    /// component the span would become an artifact of <i>who is painting</i> — ~20s on StatusDock's
    /// 1s ticker, ~0.2s in <c>LlmStreamView</c> at 90 tok/s.
    /// </para>
    /// </remarks>
    internal sealed class ThroughputRing
    {
        /// <summary>A bucket's accrued deltas. Absent until something lands in it.</summary>
        private readonly record struct Bucket(int Tokens, double Milliseconds);

        private readonly Bucket?[] _buckets = new Bucket?[ThroughputSnapshot.HistoryBuckets];

        /// <summary>
        /// The absolute index of the newest bucket the ring holds — its right-hand edge. Null
        /// until the first reading arrives, which is what makes an untouched ring report absence
        /// rather than a row of zeroes.
        /// </summary>
        private long? _newest;

        /// <summary>
        /// Folds one bucket's worth of already-differenced generation into the ring.
        /// </summary>
        /// <param name="tokens">Tokens generated since the previous reading (<c>Δpredicted_n</c>).</param>
        /// <param name="milliseconds">Server-measured time those tokens took (<c>Δpredicted_ms</c>).</param>
        /// <param name="arrival">The reading's producer-supplied arrival stamp; it alone decides the bucket.</param>
        public void Add(int tokens, double milliseconds, TimeSpan arrival)
        {
            var index = (long)(arrival.TotalMilliseconds / ThroughputSnapshot.HistoryBucketMs);

            if (_newest is not { } newest)
            {
                _newest = index;
            }
            else if (index > newest)
            {
                // Time moved on: every bucket between the old edge and the new one saw nothing, so
                // clear them to absence rather than leaving a stale reading to be re-reported.
                var stale = Math.Min(index - newest, ThroughputSnapshot.HistoryBuckets);
                for (var i = 1L; i <= stale; i++)
                    _buckets[Slot(newest + i)] = null;
                _newest = index;
            }
            else if (index <= newest - ThroughputSnapshot.HistoryBuckets)
            {
                // Older than the ring's span. Arrivals are monotonic, so this is unreachable in
                // practice; dropping it is still the only honest answer if it ever happens.
                return;
            }

            var bucket = _buckets[Slot(index)] ?? new Bucket(0, 0);
            _buckets[Slot(index)] = bucket with
            {
                Tokens = bucket.Tokens + tokens,
                Milliseconds = bucket.Milliseconds + milliseconds,
            };
        }

        /// <summary>Forgets the run's history. The next reading re-anchors the ring.</summary>
        public void Clear()
        {
            Array.Clear(_buckets);
            _newest = null;
        }

        /// <summary>
        /// The ring's buckets oldest-first, always exactly
        /// <see cref="ThroughputSnapshot.HistoryBuckets"/> long so the sparkline can reserve its
        /// width from the first render and never reflow the text beside it. A bucket that measured
        /// nothing is null.
        /// </summary>
        public IReadOnlyList<double?> Read()
        {
            var rates = new double?[ThroughputSnapshot.HistoryBuckets];
            if (_newest is not { } newest)
                return rates;

            for (var i = 0; i < ThroughputSnapshot.HistoryBuckets; i++)
            {
                var index = newest - (ThroughputSnapshot.HistoryBuckets - 1 - i);
                if (index >= 0 && _buckets[Slot(index)] is { } bucket)
                    rates[i] = ThroughputMath.Rate(bucket.Tokens, bucket.Milliseconds);
            }

            return rates;
        }

        private static int Slot(long index) => (int)(index % ThroughputSnapshot.HistoryBuckets);
    }
}
