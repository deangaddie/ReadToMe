using System.Collections.Concurrent;

namespace Read2Me.Services.Queueing
{
    public enum QueueItemStatus { Queued, Processing }

    /// <summary>
    /// The common queue state machine shared by the Character and Audio queues:
    /// per-key queued/processing status, terminal outcomes, single-reader
    /// elapsed-time tracking, and a rolling-average ETA. Queue-specific extras
    /// (resolved overlay, cache-bust versions) are composed alongside this store,
    /// not pushed into it.
    /// </summary>
    /// <typeparam name="TKey">Item key, e.g. ParagraphKey or AudioItemKey.</typeparam>
    /// <typeparam name="TOutcome">Terminal outcome, e.g. ParagraphOutcome or AudioItemOutcome.</typeparam>
    public sealed class QueueStateStore<TKey, TOutcome>
        where TKey : notnull
        where TOutcome : class
    {
        private readonly ConcurrentDictionary<TKey, QueueItemStatus> _status = new();
        private readonly ConcurrentDictionary<TKey, TOutcome> _outcomes = new();
        private readonly QueueMetrics _metrics = new();

        private DateTimeOffset? _processingStartedAt;

        /// <summary>Adds the key as Queued, clearing any prior outcome. False if already tracked.</summary>
        public bool TryMarkQueued(TKey key)
        {
            _outcomes.TryRemove(key, out _);
            return _status.TryAdd(key, QueueItemStatus.Queued);
        }

        /// <summary>
        /// Returns a currently-tracked (typically Processing) key to Queued and clears any prior
        /// outcome, so an interrupted item can be re-driven. Stops the elapsed clock.
        /// </summary>
        public void ReturnToQueued(TKey key)
        {
            _outcomes.TryRemove(key, out _);
            _status[key] = QueueItemStatus.Queued;
            ClearProcessing();
        }

        /// <summary>Moves the key to Processing and starts the single-reader elapsed clock.</summary>
        public void MarkProcessing(TKey key)
        {
            _status[key] = QueueItemStatus.Processing;
            _processingStartedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Ends the key's turn in the queue after real work was done: removes it from active
        /// tracking and records a completion against the rolling average. A non-null
        /// <paramref name="outcome"/> is stamped as the terminal marker; a null one *clears* any
        /// prior marker, so a completion can never leave a stale badge behind. If
        /// <paramref name="elapsedSeconds"/> is null the elapsed time is measured from the last
        /// <see cref="MarkProcessing"/> call — null never means "do not record".
        /// </summary>
        public void Settle(TKey key, TOutcome? outcome = null, double? elapsedSeconds = null)
        {
            if (outcome is null)
                _outcomes.TryRemove(key, out _);
            else
                _outcomes[key] = outcome;

            _status.TryRemove(key, out _);

            var elapsed = elapsedSeconds ?? CurrentElapsedSeconds();
            ClearProcessing();
            _metrics.RecordCompletion(elapsed);
        }

        /// <summary>
        /// Ends the key's turn after the work was aborted: stamps the terminal outcome and removes
        /// the key from active status <em>without</em> recording a completion.
        /// <para>
        /// The policy the two names encode: <see cref="Settle"/> is terminal-and-did-work and counts
        /// toward the rolling average; <c>Abandon</c> is aborted and does not. The average feeds an
        /// ETA of <c>queuedCount * avg</c>, so it must predict how long a remaining queued item takes
        /// to drain. An abandoned item did no measurable work and commonly returns through requeue —
        /// counting it would double-bill the same item.
        /// </para>
        /// </summary>
        public void Abandon(TKey key, TOutcome outcome)
        {
            _outcomes[key] = outcome;
            _status.TryRemove(key, out _);
            ClearProcessing();
        }

        /// <summary>Clears any outcome for the key. Returns whether anything was removed.</summary>
        public bool ClearOutcome(TKey key) => _outcomes.TryRemove(key, out _);

        public QueueItemStatus? StatusOf(TKey key)
            => _status.TryGetValue(key, out var s) ? s : null;

        public TOutcome? OutcomeOf(TKey key)
            => _outcomes.TryGetValue(key, out var o) ? o : null;

        public (int queued, int processing) CountStatuses()
        {
            int q = 0, p = 0;
            foreach (var s in _status.Values)
            {
                if (s == QueueItemStatus.Queued) q++;
                else p++;
            }
            return (q, p);
        }

        public (int completed, double avg) Metrics() => _metrics.Read();

        /// <summary>Seconds since the current item began processing, or 0 if idle.</summary>
        public double CurrentElapsedSeconds()
            => _processingStartedAt.HasValue
                ? (DateTimeOffset.UtcNow - _processingStartedAt.Value).TotalSeconds
                : 0;

        /// <summary>
        /// Clears active (queued/processing) tracking. Outcomes survive — they are
        /// cleared only on re-queue, explicit clear, or process restart. Metrics are
        /// not reset.
        /// </summary>
        public void ClearAll()
        {
            _status.Clear();
            ClearProcessing();
        }

        private void ClearProcessing()
        {
            _processingStartedAt = null;
        }
    }
}
