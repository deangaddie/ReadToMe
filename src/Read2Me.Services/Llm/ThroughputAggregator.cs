using Read2Me.Services.Events;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// One config's share of a Throughput Run. Keyed by config id, never by name: a config renamed
    /// mid-run stays one row, and the display name is resolved at render.
    /// </summary>
    /// <param name="ConfigId">The serving config's id — the row's identity.</param>
    /// <param name="ConfigName">The name last seen on a <see cref="RequestStarted"/>, for render convenience only.</param>
    /// <param name="Requests">Requests this config served in the run, whether or not they reported timings.</param>
    /// <param name="TokensOut">Σ <c>predicted_n</c> over the requests that reported timings, or null if none did.</param>
    /// <param name="GenerationMs">Σ <c>predicted_ms</c> over the same requests, or null if none did.</param>
    public sealed record ConfigThroughput(
        int ConfigId, string ConfigName, int Requests, int? TokensOut, double? GenerationMs)
    {
        /// <summary>
        /// This config's Run Throughput: <c>Σ predicted_n ÷ Σ predicted_ms</c>. Null when nothing
        /// measurable arrived — never <c>0</c>, which would mean "it generated nothing".
        /// </summary>
        public double? TokensPerSecond => ThroughputMath.Rate(TokensOut, GenerationMs);
    }

    /// <summary>
    /// A read-only view of the current (or last) Throughput Run, pulled by whoever is painting.
    /// Absence is distinguishable from zero at every point: a figure that could not be measured is
    /// null, and a run that never happened has <see cref="HasRun"/> false.
    /// </summary>
    /// <param name="HasRun">
    /// Whether there is a run to report at all. False before the first <see cref="RunStarted"/>;
    /// true from then on, including after the run ends, because the total is at its most readable
    /// exactly when it becomes final.
    /// </param>
    /// <param name="IsRunActive">
    /// Whether the run is still open. Goes false at <see cref="RunEnded"/>, at which point live
    /// figures blank while the headline and breakdown below persist — a frozen "now 87.3 tok/s"
    /// beside an idle queue reads as current and is a lie.
    /// </param>
    /// <param name="RunThroughput">
    /// The headline: <c>Σ predicted_n ÷ Σ predicted_ms</c> across every request in the run,
    /// summing the primitives and dividing once at the end. Null when nothing was measurable.
    /// </param>
    /// <param name="PerConfig">
    /// The run's per-config breakdown, one row per config id that served a request, so an
    /// escalation from a 4b to a 26b doesn't blend into one meaningless average.
    /// </param>
    /// <param name="GenerationRate">
    /// The live figure: output tok/s over a sliding <see cref="GenerationRateWindow"/> of the
    /// in-flight request, differenced from consecutive cumulative readings. Null whenever nothing
    /// is currently measurable — between requests before the first token, and always once the run
    /// has ended, because a frozen "now 87.3 tok/s" beside an idle queue reads as current and is
    /// a lie. It says <i>steady vs stuttering</i> and nothing else; the headline is
    /// <see cref="RunThroughput"/>.
    /// </param>
    /// <param name="GenerationRateHistory">
    /// The sparkline's history: exactly <see cref="HistoryBuckets"/> buckets of
    /// <see cref="HistoryBucketMs"/>ms, oldest first, spanning the last
    /// <see cref="HistorySpan"/> of the run. A bucket nothing was measured in is null — an empty
    /// bucket is not a <c>0</c> bucket. All null once the run has ended, or before it has
    /// generated anything.
    /// </param>
    public sealed record ThroughputSnapshot(
        bool HasRun,
        bool IsRunActive,
        double? RunThroughput,
        IReadOnlyList<ConfigThroughput> PerConfig,
        double? GenerationRate,
        IReadOnlyList<double?> GenerationRateHistory)
    {
        /// <summary>Ticks in the sparkline, and therefore buckets in the ring behind it.</summary>
        public const int HistoryBuckets = 20;

        /// <summary>How much of a run one bucket covers.</summary>
        public const int HistoryBucketMs = 500;

        /// <summary>The span the sparkline charts: 20 × 500ms.</summary>
        public static readonly TimeSpan HistorySpan =
            TimeSpan.FromMilliseconds(HistoryBuckets * HistoryBucketMs);

        /// <summary>
        /// The window <see cref="GenerationRate"/> is measured over. Responsive without jitter
        /// across the 4b/26b spread; it computes over a partial window before it fills, so a
        /// request shorter than the window still shows a rate rather than nothing.
        /// </summary>
        public static readonly TimeSpan GenerationRateWindow = TimeSpan.FromSeconds(3);

        /// <summary>
        /// A history of nothing: the sparkline's box, reserved at full width, with every bucket
        /// absent. What a run that has ended, or has yet to generate a token, reports.
        /// </summary>
        public static IReadOnlyList<double?> AbsentHistory() => new double?[HistoryBuckets];

        /// <summary>The state before any run has ever started: nothing to report, nothing to blank.</summary>
        public static readonly ThroughputSnapshot Empty =
            new(false, false, null, [], null, AbsentHistory());
    }

    /// <summary>
    /// Answers "how fast is this run going, and which config did the work" for every surface, by
    /// listening to the LLM stream bus and nothing else. It knows nothing of the voice batch
    /// runner, the attribution queue, or any surface — every producer converges on
    /// <see cref="RunStarted"/>/<see cref="RunEnded"/>, so there are no special cases here.
    /// </summary>
    /// <remarks>
    /// <b>Pull, not push.</b> This raises no event and exposes only <see cref="Snapshot"/>.
    /// Components read it when they <i>already</i> paint, which makes per-token render
    /// amplification structurally impossible rather than merely throttled, and keeps this class
    /// clock-free — a puller brings its own clock. Do not add a <c>Changed</c> event, and do not
    /// add a timer.
    /// <para>
    /// <b>Sum the primitives; divide at the end.</b> Averaging per-request rates, or reading the
    /// server's ready-made <c>predicted_per_second</c>, would make a request rate and a run total
    /// disagree about the same work (ADR 0003).
    /// </para>
    /// <para>
    /// <b>App-scoped singleton, and cross-circuit sharing is intentional.</b> One queue runs at a
    /// time on one GPU, so two tabs <i>should</i> see the same totals. In-memory only: an
    /// <c>LlmServerConfig</c> is mutable in place, so persisted per-config history would silently
    /// average across a model swap under one id.
    /// </para>
    /// <para>
    /// <b>Clock-free.</b> The live figures are driven entirely by
    /// <see cref="LlmTimingsSample"/>'s producer-supplied arrival stamps. Do not reach for
    /// <c>DateTime.Now</c> to advance the ring: the same event sequence must yield the same ring
    /// however long after the fact it is read, or two tabs would chart one run differently.
    /// </para>
    /// </remarks>
    public sealed class ThroughputAggregator
    {
        private readonly Lock _gate = new();

        /// <summary>Insertion-ordered so the breakdown reads in the order configs entered the run.</summary>
        private readonly List<int> _order = [];
        private readonly Dictionary<int, ConfigThroughput> _perConfig = [];
        private readonly ThroughputRing _ring = new();

        private bool _hasRun;
        private bool _isRunActive;

        /// <summary>
        /// The config that served the in-flight request, latched from <see cref="RequestStarted"/>
        /// because config identity is known at request start and does not ride
        /// <see cref="StreamCompleted"/>.
        /// </summary>
        private (int Id, string Name)? _pending;

        /// <summary>
        /// The in-flight request's readings, for the live Generation Rate. One per request, because
        /// llama.cpp's counters restart at each request and differencing across that boundary would
        /// read as a burst of negative work.
        /// </summary>
        private TimingsAccumulator? _live;

        /// <summary>
        /// The previous reading of the in-flight request, so a sample can be differenced into the
        /// deltas the ring buckets. Cumulative-not-additive. Null until the request's first reading
        /// arrives — that reading is the baseline and is charted as nothing, because the interval it
        /// would describe is the one <c>predicted_ms</c> never measures. See <see cref="Handle(LlmTimingsSample)"/>.
        /// </summary>
        private (int Tokens, double Milliseconds)? _lastReading;

        public ThroughputAggregator(
            EventBroadcaster<LlmStreamEvent> broadcaster,
            EventBroadcaster<LlmTimingsSample> samples)
        {
            broadcaster.Event += Handle;
            samples.Event += Handle;
        }

        /// <summary>
        /// The current or last run's figures. Cheap enough to pull on every paint: it materialises
        /// the breakdown so a caller can never observe a run mutating mid-render.
        /// </summary>
        public ThroughputSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    if (!_hasRun)
                        return ThroughputSnapshot.Empty;

                    var rows = _order.Select(id => _perConfig[id]).ToList();

                    // The live figures blank off the run being over, while the headline and the
                    // breakdown below persist — the total is at its most readable the moment it is
                    // final, but a live rate that stopped moving is no longer live.
                    return new ThroughputSnapshot(
                        true, _isRunActive, RunTotal(rows), rows,
                        _isRunActive ? _live?.WindowRate(ThroughputSnapshot.GenerationRateWindow) : null,
                        _isRunActive ? _ring.Read() : ThroughputSnapshot.AbsentHistory());
                }
            }
        }

        /// <summary>
        /// The headline, summed across configs from the same primitives the rows hold. Null unless
        /// something measurable arrived, so "we don't know" never collapses into <c>0 tok/s</c>.
        /// </summary>
        private static double? RunTotal(List<ConfigThroughput> rows)
        {
            int? tokens = null;
            double? ms = null;
            foreach (var row in rows)
            {
                if (row.TokensOut is { } n)
                    tokens = (tokens ?? 0) + n;
                if (row.GenerationMs is { } m)
                    ms = (ms ?? 0) + m;
            }

            return ThroughputMath.Rate(tokens, ms);
        }

        private void Handle(LlmStreamEvent e)
        {
            lock (_gate)
            {
                switch (e)
                {
                    case RunStarted:
                        // A new run resets the last one's figures — this is why a total never needs
                        // a clear button. The ring goes with them: its buckets chart this run.
                        _order.Clear();
                        _perConfig.Clear();
                        _ring.Clear();
                        _pending = null;
                        _live = null;
                        _lastReading = null;
                        _hasRun = true;
                        _isRunActive = true;
                        break;

                    case RequestStarted r when _isRunActive:
                        _pending = (r.ConfigId, r.ConfigName);
                        // Rebaseline the live figures: the next request's counters start from zero
                        // again, so differencing across the boundary would go negative.
                        _live = new TimingsAccumulator();
                        _lastReading = null;
                        var row = Row(r.ConfigId, r.ConfigName);
                        _perConfig[r.ConfigId] = row with { Requests = row.Requests + 1 };
                        break;

                    case StreamCompleted s when _isRunActive && _pending is { } config:
                        Record(config, s.TokensOut, s.GenerationMs);
                        _pending = null;
                        break;

                    // An aborted request's last reading folds in exactly like a completed one's:
                    // real work, really measured. Only the outcome differed, and this aggregator
                    // does not report outcomes. Unlatching _pending is what stops the StreamFailed
                    // that follows a failure from double-counting the same request.
                    case StreamAborted a when _isRunActive && _pending is { } aborted:
                        Record(aborted, a.TokensOut, a.GenerationMs);
                        _pending = null;
                        break;

                    case RunEnded:
                        // Figures persist: the total is at its most readable the moment it is final.
                        // The live readings do not — nothing is generating any more.
                        _isRunActive = false;
                        _pending = null;
                        _live = null;
                        _lastReading = null;
                        break;
                }
            }
        }

        /// <summary>
        /// Folds one chunk's reading into the live figures. Readings are <b>cumulative running
        /// totals</b> restated by every chunk, so this differences consecutive ones rather than
        /// summing them — summing would double-count every token and produce a plausible-looking
        /// wrong number (ADR 0003).
        /// </summary>
        /// <remarks>
        /// A sample outside a run, or before any <see cref="RequestStarted"/> latched a request, is
        /// ignored: there is nothing for it to be part of.
        /// </remarks>
        private void Handle(LlmTimingsSample sample)
        {
            lock (_gate)
            {
                if (!_isRunActive || _live is null)
                    return;

                _live.Add(sample.Timings, sample.Arrival);

                if (sample.Timings is not { PredictedN: { } n, PredictedMs: { } ms })
                    return;

                // The first reading of a request establishes the baseline and charts nothing. Its
                // counters did not start at zero at a measurable instant: predicted_ms clocks *from*
                // the first token, so the interval this reading would describe — start-of-generation
                // → first token — is the one interval the server explicitly excludes. llama.cpp
                // states the first chunk as predicted_n:1, predicted_ms:0.001, and differencing that
                // against the origin charts 1,000,000 tok/s. That is not an outlier to clamp or drop
                // — it is not a measurement. Consequently the ring charts from the second reading
                // onward, and a request that yields only one contributes no bucket at all: absence,
                // never a fabricated rate.
                if (_lastReading is not { } last)
                {
                    _lastReading = (n, ms);
                    return;
                }

                var (lastN, lastMs) = last;

                // A counter that went backwards can only mean the reading is not part of the series
                // we were differencing. Re-anchor on it rather than charting negative generation.
                if (n < lastN || ms < lastMs)
                {
                    _lastReading = (n, ms);
                    return;
                }

                _lastReading = (n, ms);
                _ring.Add(n - lastN, ms - lastMs, sample.Arrival);
            }
        }

        /// <summary>
        /// Folds a finished request's final figures into its config's row, whether it completed or
        /// was aborted — the two differ in outcome, which is not this aggregator's business, and
        /// agree in being the server's own last measurement.
        /// </summary>
        /// <remarks>
        /// A request that reported no timings contributes no tokens and no milliseconds. It is
        /// counted in the row's <c>req</c> column (from its <see cref="RequestStarted"/>) but it
        /// cannot zero a total it never measured — which is why these fold through null rather than
        /// defaulting to <c>0</c>.
        /// </remarks>
        private void Record((int Id, string Name) config, int? tokensOut, double? generationMs)
        {
            var row = Row(config.Id, config.Name);
            _perConfig[config.Id] = row with
            {
                TokensOut = tokensOut is { } n ? (row.TokensOut ?? 0) + n : row.TokensOut,
                GenerationMs = generationMs is { } ms ? (row.GenerationMs ?? 0) + ms : row.GenerationMs,
            };
        }

        /// <summary>
        /// The config's row, created on first sight and counted once per request. Keyed by id, so a
        /// rename lands on the existing row rather than splitting it in two.
        /// </summary>
        private ConfigThroughput Row(int configId, string configName)
        {
            if (_perConfig.TryGetValue(configId, out var existing))
                return _perConfig[configId] = existing with { ConfigName = configName };

            _order.Add(configId);
            return _perConfig[configId] = new ConfigThroughput(configId, configName, 0, null, null);
        }
    }
}
