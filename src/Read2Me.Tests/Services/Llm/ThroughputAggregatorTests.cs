using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    /// <summary>
    /// The aggregator knows only the bus, so every test here publishes events and asserts the
    /// pulled snapshot — the figures a surface would display, never the internals behind them.
    /// No fake <c>ILlmClient</c>, and no clock.
    /// </summary>
    public class ThroughputAggregatorTests
    {
        private readonly EventBroadcaster<LlmStreamEvent> _bus = new();
        private readonly EventBroadcaster<LlmTimingsSample> _samples = new();
        private readonly ThroughputAggregator _aggregator;

        public ThroughputAggregatorTests() => _aggregator = new ThroughputAggregator(_bus, _samples);

        private void Publish(params LlmStreamEvent[] events)
        {
            foreach (var e in events)
                _bus.Publish(e);
        }

        /// <summary>
        /// A chunk's reading as the producer would stamp it. Timestamps are fed, never read from a
        /// clock — that is what lets these tests be plain arrays.
        /// </summary>
        private void Sample(int predictedN, double predictedMs, double arrivalMs) =>
            _samples.Publish(new LlmTimingsSample(
                new LlmTimings(CacheN: null, PromptN: null, PromptMs: null, predictedN, predictedMs),
                TimeSpan.FromMilliseconds(arrivalMs)));

        private static RequestStarted Request(int configId, string configName = "gemma-4b") =>
            new("para", "prompt", configId, configName);

        /// <summary>A request's final server figures. Tokens and ms are the only primitives that matter.</summary>
        private static StreamCompleted Completed(int? tokensOut, double? generationMs) =>
            new(TokensIn: 100, tokensOut, generationMs, TokensPerSecond: null);

        /// <summary>
        /// A request that died mid-stream, carrying the last reading the runner received. It folds
        /// in exactly like a completed one's — the outcome differed, the measurement did not.
        /// </summary>
        private static StreamAborted Aborted(int? tokensOut, double? generationMs) =>
            new(tokensOut, generationMs, TokensPerSecond: null);

        private ThroughputSnapshot Snapshot => _aggregator.Snapshot;

        // ---- The headline ----

        [Fact]
        public void ARunOfOneRequest_ReportsThatRequestsRateAsTheRunTotal()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000), new RunEnded());

            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
        }

        [Fact]
        public void ABatchOfRequests_YieldsOneRunWithATotalAcrossAllOfThem()
        {
            Publish(new RunStarted());
            for (var i = 0; i < 4; i++)
                Publish(Request(1), Completed(30, 500));
            Publish(new RunEnded());

            // 120 tokens over 2000ms — one run, not four.
            Assert.Equal(60, Snapshot.RunThroughput!.Value, 3);
            Assert.Equal(4, Assert.Single(Snapshot.PerConfig).Requests);
        }

        [Fact]
        public void RunThroughput_SumsThePrimitivesAndDividesAtTheEnd_RatherThanAveragingRates()
        {
            // Rates of 100 and 10 tok/s. Averaging them gives 55; the honest total is
            // 200 tokens ÷ 2100ms = 95.2 — the fast request did nearly all the work.
            Publish(new RunStarted(),
                Request(1), Completed(100, 1000),
                Request(1), Completed(100, 1100),
                new RunEnded());

            Assert.Equal(200 / 2100.0 * 1000, Snapshot.RunThroughput!.Value, 3);
        }

        [Fact]
        public void RunThroughput_IgnoresTheServersReadyMadePerSecondFigure()
        {
            // The server says 5 tok/s; the primitives say 90. We recompute, always.
            Publish(new RunStarted(),
                Request(1),
                new StreamCompleted(TokensIn: 10, TokensOut: 90, GenerationMs: 1000, TokensPerSecond: 5),
                new RunEnded());

            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
        }

        // ---- Per-config breakdown ----

        [Fact]
        public void ARunEscalatingAcrossTwoConfigs_YieldsTwoRows_NotABlendedAverage()
        {
            Publish(new RunStarted(),
                Request(1, "gemma-4b"), Completed(90, 1000),
                Request(2, "gemma-26b"), Completed(12, 1000),
                new RunEnded());

            var rows = Snapshot.PerConfig;
            Assert.Equal(2, rows.Count);
            Assert.Equal(90, rows[0].TokensPerSecond!.Value, 3);
            Assert.Equal("gemma-4b", rows[0].ConfigName);
            Assert.Equal(12, rows[1].TokensPerSecond!.Value, 3);
            Assert.Equal("gemma-26b", rows[1].ConfigName);
        }

        [Fact]
        public void ARunThatNeverEscalated_YieldsOneRow()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000), new RunEnded());

            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Equal(1, row.ConfigId);
            Assert.Equal(1, row.Requests);
            Assert.Equal(90, row.TokensOut);
        }

        [Fact]
        public void AConfigRenamedMidRun_StaysOneRow_KeyedById()
        {
            Publish(new RunStarted(),
                Request(1, "gemma-4b"), Completed(50, 1000),
                Request(1, "the fast one"), Completed(50, 1000),
                new RunEnded());

            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Equal(2, row.Requests);
            Assert.Equal(100, row.TokensOut);
            Assert.Equal("the fast one", row.ConfigName);
        }

        [Fact]
        public void EachRequestAttributesWhollyToTheConfigThatServedIt()
        {
            Publish(new RunStarted(),
                Request(1, "gemma-4b"), Completed(90, 1000),
                Request(2, "gemma-26b"), Completed(12, 1000),
                new RunEnded());

            Assert.Equal(90, Snapshot.PerConfig[0].TokensOut);
            Assert.Equal(12, Snapshot.PerConfig[1].TokensOut);
        }

        // ---- Absence ----

        [Fact]
        public void ARequestWithNoTimings_ContributesNothing_AndDoesNotZeroTheTotal()
        {
            Publish(new RunStarted(),
                Request(1), Completed(90, 1000),
                Request(1), Completed(null, null),   // a backend that sent no timings
                new RunEnded());

            // Still 90 tok/s — the unmeasured request neither adds tokens nor stretches the clock.
            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Equal(2, row.Requests);
            Assert.Equal(90, row.TokensOut);
        }

        [Fact]
        public void ARunWhoseRequestsAllLackTimings_ReportsAbsence_NotZero()
        {
            Publish(new RunStarted(), Request(1), Completed(null, null), new RunEnded());

            Assert.Null(Snapshot.RunThroughput);
            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Null(row.TokensOut);
            Assert.Null(row.GenerationMs);
            Assert.Null(row.TokensPerSecond);
            Assert.Equal(1, row.Requests);   // it did happen — absence is the rate, not the request
        }

        [Fact]
        public void AFullyCachedPromptsNullFigures_RenderAsAbsence_RatherThanZero()
        {
            // llama.cpp's unguarded divide-by-zero serializes as null on a fully cached prompt.
            Publish(new RunStarted(), Request(1), Completed(0, 0), new RunEnded());

            Assert.Null(Snapshot.RunThroughput);
        }

        [Fact]
        public void BeforeAnyRunStarts_TheSnapshotReportsNoRunAtAll()
        {
            Assert.False(Snapshot.HasRun);
            Assert.False(Snapshot.IsRunActive);
            Assert.Null(Snapshot.RunThroughput);
            Assert.Empty(Snapshot.PerConfig);
        }

        // ---- Lifetime ----

        [Fact]
        public void RunStarted_ResetsThePreviousRunsFigures()
        {
            Publish(new RunStarted(), Request(1, "gemma-4b"), Completed(90, 1000), new RunEnded());
            Publish(new RunStarted(), Request(2, "gemma-26b"), Completed(12, 1000));

            Assert.Equal(12, Snapshot.RunThroughput!.Value, 3);
            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Equal(2, row.ConfigId);
        }

        [Fact]
        public void RunStarted_ClearsTheFiguresEvenBeforeAnyRequestArrives()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000), new RunEnded());
            Publish(new RunStarted());

            // The last run's total must not linger beside the new run that replaced it.
            Assert.True(Snapshot.HasRun);
            Assert.True(Snapshot.IsRunActive);
            Assert.Null(Snapshot.RunThroughput);
            Assert.Empty(Snapshot.PerConfig);
        }

        [Fact]
        public void AfterRunEnded_TheTotalAndBreakdownPersist_AndLiveFiguresReportAbsence()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000), new RunEnded());

            Assert.True(Snapshot.HasRun);
            Assert.False(Snapshot.IsRunActive);   // live figures blank off this
            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
            Assert.Single(Snapshot.PerConfig);
        }

        [Fact]
        public void MidRun_TheSnapshotReportsTheRunActive_SoLiveFiguresCanShow()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000));

            Assert.True(Snapshot.IsRunActive);
            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
        }

        [Fact]
        public void EventsArrivingOutsideARun_AreIgnored()
        {
            // A producer that never bracketed its work has no run to contribute to; inventing one
            // would give the next RunStarted a total it did not measure.
            Publish(Request(1), Completed(90, 1000));

            Assert.False(Snapshot.HasRun);
            Assert.Empty(Snapshot.PerConfig);
        }

        [Fact]
        public void EventsArrivingAfterRunEnded_DoNotDisturbTheFinalFigures()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000), new RunEnded());
            Publish(Request(2, "gemma-26b"), Completed(999, 1000));

            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
            Assert.Single(Snapshot.PerConfig);
        }

        // ---- Aborts and failures ----

        [Fact]
        public void AnAbortedRequestsLastReceivedReading_StillCountsTowardTheTotal()
        {
            // The watchdog killed it mid-generation; its last timings_per_token reading is real
            // work, really measured, and the run total would under-count without it. The runner
            // publishes the figures on StreamAborted and the error on StreamFailed — measurement
            // and outcome are separate events, so a cancel can send the first without the second.
            // The abort's rate deliberately differs from the completed request's (150 vs 60 tok/s):
            // dropping it must move the total, or this test cannot tell the two behaviours apart.
            Publish(new RunStarted(),
                Request(1), Completed(60, 1000),
                Request(1), Aborted(30, 200),          // the last reading before the abort
                new StreamFailed("watchdog killed the request"),
                new RunEnded());

            Assert.Equal(90 / 1200.0 * 1000, Snapshot.RunThroughput!.Value, 3);
            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Equal(90, row.TokensOut);
            Assert.Equal(2, row.Requests);
        }

        [Fact]
        public void ACancelledRequest_ContributesItsReading_WithoutAnyStreamFailed()
        {
            // The cancel path publishes StreamAborted alone — no StreamFailed, because a cancel is
            // not a service failure and must surface no error. The measurement still lands.
            Publish(new RunStarted(),
                Request(1), Aborted(45, 500),
                new RunEnded());

            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
            Assert.Equal(45, Assert.Single(Snapshot.PerConfig).TokensOut);
        }

        [Fact]
        public void ARequestAbortedBeforeAnyReading_ContributesNothing_AndDoesNotZeroTheTotal()
        {
            // It died during model load, measuring nothing. Absence must not drag the total down:
            // folding it in as 0 tokens over 0ms would be a lie about work that was never measured.
            Publish(new RunStarted(),
                Request(1), Completed(90, 1000),
                Request(1), Aborted(null, null),
                new RunEnded());

            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Equal(90, row.TokensOut);
            // Still a request that happened, even though nothing about it was measurable.
            Assert.Equal(2, row.Requests);
        }

        [Fact]
        public void ARunOfOnlyAnAbortedUnmeasuredRequest_ReportsAbsence_NotZero()
        {
            Publish(new RunStarted(), Request(1), Aborted(null, null), new RunEnded());

            Assert.Null(Snapshot.RunThroughput);
            Assert.Null(Assert.Single(Snapshot.PerConfig).TokensPerSecond);
        }

        [Fact]
        public void TheStreamFailedFollowingAnAbort_DoesNotDoubleCountTheRequest()
        {
            // The failure path publishes both events for one request. Only the first carries
            // figures, and the aggregator unlatches on it, so the second folds in nothing.
            Publish(new RunStarted(),
                Request(1), Aborted(30, 500), new StreamFailed("boom"),
                new RunEnded());

            var row = Assert.Single(Snapshot.PerConfig);
            Assert.Equal(30, row.TokensOut);
            Assert.Equal(1, row.Requests);
        }

        [Fact]
        public void AnAbortedRequest_StillCountsInItsConfigsReqColumn()
        {
            Publish(new RunStarted(),
                Request(1), Aborted(30, 500),
                Request(1), Aborted(null, null),
                new RunEnded());

            Assert.Equal(2, Assert.Single(Snapshot.PerConfig).Requests);
        }

        [Fact]
        public void AnAbortedRunStillEnds_AndItsTotalPersistsAsTheFinalResult()
        {
            // Every producer publishes RunEnded from a finally, cancelled or not — an unclosed run
            // would strand the next one's total.
            Publish(new RunStarted(), Request(1), Aborted(45, 500), new RunEnded());

            Assert.True(Snapshot.HasRun);
            Assert.False(Snapshot.IsRunActive);
            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
            // Live figures blank; the total persists at the moment it becomes final.
            Assert.Null(Snapshot.GenerationRate);
        }

        [Fact]
        public void AnAbortAcrossTwoConfigs_KeepsTheEscalationsCostVisible()
        {
            // The 4b's killed attempt is real work the 26b's retry did not do. Blending or dropping
            // it would hide what the escalation actually cost.
            Publish(new RunStarted(),
                Request(1, "gemma-4b"), Aborted(30, 500),
                Request(2, "gemma-26b"), Completed(60, 1000),
                new RunEnded());

            Assert.Collection(Snapshot.PerConfig,
                r => Assert.Equal(30, r.TokensOut),
                r => Assert.Equal(60, r.TokensOut));
        }

        [Fact]
        public void ARequestThatFailedWithoutCompleting_LeavesTheTotalIntact()
        {
            Publish(new RunStarted(),
                Request(1), Completed(90, 1000),
                Request(1), new StreamFailed("connection reset"),
                new RunEnded());

            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
        }

        [Fact]
        public void AStreamCompletedWithNoPrecedingRequest_IsIgnored()
        {
            // Nothing latched a config, so there is nothing to attribute the work to.
            Publish(new RunStarted(), Completed(90, 1000), new RunEnded());

            Assert.Null(Snapshot.RunThroughput);
            Assert.Empty(Snapshot.PerConfig);
        }

        // ---- The live Generation Rate ----

        [Fact]
        public void TheLiveRate_IsTheInFlightRequestsRate_OverTheSlidingWindow()
        {
            Publish(new RunStarted(), Request(1));
            Sample(predictedN: 90, predictedMs: 1000, arrivalMs: 1000);

            // A window that hasn't filled yet computes over what has arrived, differenced against
            // the start of generation — so a short request still shows a real rate.
            Assert.Equal(90, Snapshot.GenerationRate!.Value, 3);
        }

        [Fact]
        public void TheLiveRate_DifferencesTheCumulativeReadings_RatherThanAveragingTheRequest()
        {
            // The request averages 400 ÷ 4500ms = 88.9 tok/s, but the last 3s did 310 tokens in
            // 3500ms of generation. The window says what is happening now, not overall.
            Publish(new RunStarted(), Request(1));
            Sample(90, 1000, arrivalMs: 1000);
            Sample(400, 4500, arrivalMs: 4500);

            Assert.Equal(310 / 3500.0 * 1000, Snapshot.GenerationRate!.Value, 3);
        }

        [Fact]
        public void ANewRequest_RebaselinesTheLiveRate_BecauseTheServersCountersRestart()
        {
            // llama.cpp's counters are cumulative *within a request* and start again at the next
            // one. Differencing across that boundary would read as negative generation.
            Publish(new RunStarted(), Request(1));
            Sample(100, 1000, arrivalMs: 1000);
            Publish(Completed(100, 1000), Request(2, "gemma-26b"));
            Sample(10, 100, arrivalMs: 1500);

            Assert.Equal(100, Snapshot.GenerationRate!.Value, 3);
        }

        [Fact]
        public void BeforeItsFirstToken_ARequestHasNoLiveRate_AndReportsAbsenceNotZero()
        {
            // Nothing has generated yet — a 26b evaluating a prompt is not running at 0 tok/s.
            Publish(new RunStarted(), Request(1));

            Assert.Null(Snapshot.GenerationRate);
        }

        [Fact]
        public void SamplesArrivingWithNoRequest_AreIgnored()
        {
            Publish(new RunStarted());
            Sample(90, 1000, arrivalMs: 1000);

            Assert.Null(Snapshot.GenerationRate);
            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
        }

        [Fact]
        public void SamplesArrivingOutsideARun_AreIgnored()
        {
            Sample(90, 1000, arrivalMs: 1000);

            Assert.False(Snapshot.HasRun);
            Assert.Null(Snapshot.GenerationRate);
        }

        [Fact]
        public void AFullyCachedPromptsNullReading_ContributesNothing_RatherThanZero()
        {
            Publish(new RunStarted(), Request(1));
            _samples.Publish(new LlmTimingsSample(
                new LlmTimings(null, null, null, PredictedN: null, PredictedMs: null),
                TimeSpan.FromMilliseconds(500)));

            Assert.Null(Snapshot.GenerationRate);
            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
        }

        // ---- The ring ----

        [Fact]
        public void TheSnapshotExposes_TwentyBucketsOverATenSecondSpan()
        {
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 100);

            Assert.Equal(20, ThroughputSnapshot.HistoryBuckets);
            Assert.Equal(TimeSpan.FromSeconds(10), ThroughputSnapshot.HistorySpan);
            // The width is reserved from the first render, so the list is always full length.
            Assert.Equal(20, Snapshot.GenerationRateHistory.Count);
        }

        [Fact]
        public void EachBucket_ChartsTheGenerationThatLandedInIts500msSlice()
        {
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 100);    // the baseline: charts nothing
            Sample(30, 300, arrivalMs: 600);    // bucket 1: +20 in 200ms → 100 tok/s
            Sample(35, 800, arrivalMs: 1100);   // bucket 2: +5 in 500ms → 10 tok/s — a stutter

            var ring = Snapshot.GenerationRateHistory;
            Assert.Equal(100, ring[18]!.Value, 3);
            Assert.Equal(10, ring[19]!.Value, 3);
            // Oldest-first, and only the buckets that saw work are populated.
            Assert.All(ring.Take(18), Assert.Null);
        }

        [Fact]
        public void TheRing_DifferencesConsecutiveReadings_RatherThanSummingThem()
        {
            // Cumulative, not additive. Summing the running totals would chart bucket 1 as
            // 60 tokens ÷ 200ms = 300 tok/s; it actually generated 50 tokens in 100ms.
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 100);
            Sample(60, 200, arrivalMs: 600);

            Assert.Equal(500, Snapshot.GenerationRateHistory[19]!.Value, 3);
        }

        [Fact]
        public void ABucketNothingArrivedIn_IsAbsent_NotAZeroBucket()
        {
            // The gap is the signal: a bucket that measured nothing is not a bucket that measured
            // no tokens. Collapsing it to 0 would make a stall and a silence identical.
            Publish(new RunStarted(), Request(1));
            Sample(5, 50, arrivalMs: 100);      // the baseline: charts nothing
            Sample(10, 100, arrivalMs: 300);    // bucket 0
            Sample(20, 200, arrivalMs: 1100);   // bucket 2 — nothing arrived in bucket 1

            var ring = Snapshot.GenerationRateHistory;
            Assert.Equal(100, ring[17]!.Value, 3);
            Assert.Null(ring[18]);
            Assert.Equal(100, ring[19]!.Value, 3);
        }

        // ---- The baseline reading (ticket 12) ----

        /// <summary>
        /// Any rate above this is arithmetically impossible on the hardware this app targets — an
        /// 8 GB RTX 3070 runs a 4b at ~90 tok/s. The degenerate first chunk charted 1,000,000.
        /// </summary>
        private const double SaneCeiling = 1000;

        [Fact]
        public void TheFirstReadingOfARequest_SetsTheBaseline_AndChartsNoBucket()
        {
            // predicted_ms clocks *from* the first token, so the interval this reading would
            // describe — start-of-generation → first token — is the one the server never measures.
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 100);

            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
        }

        [Fact]
        public void TheForksDegenerateFirstChunk_ChartsNoAbsurdRate()
        {
            // The real sequence off the running fork. Differenced against the origin, the first
            // reading is 1 token ÷ 0.001ms = 1,000,000 tok/s — which, scaled to the window's own
            // max, crushes every real 13 tok/s bar to 1px for the ring's whole 10s span.
            //
            // The arrivals put that first reading in a bucket of its own, which is what makes the
            // spike visible rather than diluted: the first token lands whenever prompt eval happens
            // to finish, and the tokens after it cross into the next bucket.
            Publish(new RunStarted(), Request(1));
            Sample(1, 0.001, arrivalMs: 400);
            Sample(2, 93.503, arrivalMs: 600);
            Sample(3, 183.518, arrivalMs: 700);
            Sample(4, 262.965, arrivalMs: 800);

            var charted = Snapshot.GenerationRateHistory.Where(r => r is not null).ToList();

            // Three tokens over the 262.964ms the server actually measured between the baseline and
            // the last reading — ~11.4 tok/s, which is what the fork was really doing.
            Assert.Equal(3 / 262.964 * 1000, Assert.Single(charted)!.Value, 3);
            Assert.All(charted, r => Assert.InRange(r!.Value, 0, SaneCeiling));
        }

        [Fact]
        public void ARequestYieldingExactlyOneReading_ContributesNoBucket()
        {
            // A token-1 abort. Absence is the honest answer; a fabricated rate is not.
            Publish(new RunStarted(), Request(1));
            Sample(1, 0.001, arrivalMs: 100);
            Publish(new StreamFailed("aborted at the first token"));

            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
        }

        [Fact]
        public void ARunOfManyRequests_InjectsNoSpikeAtAnyRequestBoundary()
        {
            // _lastReading rebaselines on every RequestStarted, so an origin-differenced first
            // reading would inject a fresh 1,000,000 tok/s spike per request — and an attribution
            // queue would spike faster than the last one could age out of the ring.
            Publish(new RunStarted());
            for (var i = 0; i < 3; i++)
            {
                // Each request's first reading lands in a bucket of its own, as it does on the fork.
                var offset = 400 + i * 1000;
                Publish(Request(1));
                Sample(1, 0.001, arrivalMs: offset);
                Sample(2, 93.503, arrivalMs: offset + 200);
                Sample(3, 183.518, arrivalMs: offset + 300);
                Publish(Completed(3, 183.518));
            }

            var charted = Snapshot.GenerationRateHistory.Where(r => r is not null).ToList();

            Assert.Equal(3, charted.Count);   // one bucket per request, none of them a spike
            Assert.All(charted, r => Assert.InRange(r!.Value, 0, SaneCeiling));
        }

        [Fact]
        public void TheSameEventSequence_YieldsTheSameRing_RegardlessOfWhenItIsRead()
        {
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 100);
            Sample(30, 300, arrivalMs: 600);

            var first = Snapshot.GenerationRateHistory;
            var second = Snapshot.GenerationRateHistory;

            // No clock advances the ring, so two tabs pulling at different moments — or the same
            // tab pulling twice — chart the run identically.
            Assert.Equal(first, second);
            Assert.Equal(first, Snapshot.GenerationRateHistory);
        }

        [Fact]
        public void TheRingIsBucketedOnTheFedArrivalStamps_NotOnAClockTheAggregatorReads()
        {
            // These stamps sit hours from any wall clock. A ring that read a clock would drop them
            // all as ancient; an arrival-driven one charts them exactly as it charts any run.
            var hours = TimeSpan.FromHours(5).TotalMilliseconds;
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: hours + 100);    // the baseline: charts nothing
            Sample(30, 300, arrivalMs: hours + 600);
            Sample(50, 500, arrivalMs: hours + 1100);

            var ring = Snapshot.GenerationRateHistory;
            Assert.Equal(100, ring[18]!.Value, 3);
            Assert.Equal(100, ring[19]!.Value, 3);
        }

        [Fact]
        public void GenerationOlderThanTheSpan_FallsOffTheRing()
        {
            Publish(new RunStarted(), Request(1));
            Sample(5, 50, arrivalMs: 100);          // the baseline: charts nothing
            Sample(10, 100, arrivalMs: 300);        // bucket 0
            Sample(500, 5000, arrivalMs: 12_600);   // bucket 25 — more than 10s later

            var ring = Snapshot.GenerationRateHistory;
            Assert.NotNull(ring[19]);
            Assert.All(ring.Take(19), Assert.Null);
        }

        [Fact]
        public void TheRingSpansTheWholeRun_NotJustOneRequest()
        {
            // Arrival stamps run on one origin across the run's requests, so a batch's buckets
            // advance rather than colliding on top of each other. Each request baselines on its own
            // first reading, so each needs a second one before it charts anything.
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 100);    // request 1's baseline
            Sample(20, 200, arrivalMs: 600);    // bucket 1
            Publish(Completed(20, 200), Request(1));
            Sample(10, 100, arrivalMs: 1100);   // request 2's baseline
            Sample(20, 200, arrivalMs: 1600);   // bucket 3

            var ring = Snapshot.GenerationRateHistory;
            Assert.Equal(100, ring[17]!.Value, 3);
            Assert.Equal(100, ring[19]!.Value, 3);
        }

        // ---- Lifetime of the live figures ----

        [Fact]
        public void AfterRunEnded_TheLiveRateAndRingReportAbsence_WhileTheHeadlinePersists()
        {
            Publish(new RunStarted(), Request(1));
            Sample(90, 1000, arrivalMs: 1000);
            Publish(Completed(90, 1000), new RunEnded());

            // A frozen "now 87.3 tok/s" beside an idle queue reads as current and is a lie.
            Assert.Null(Snapshot.GenerationRate);
            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
            Assert.Equal(20, Snapshot.GenerationRateHistory.Count);   // the box stays reserved

            // ...but the total is at its most readable the moment it becomes final.
            Assert.Equal(90, Snapshot.RunThroughput!.Value, 3);
            Assert.Single(Snapshot.PerConfig);
        }

        [Fact]
        public void SamplesArrivingAfterRunEnded_DoNotReviveTheLiveFigures()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000), new RunEnded());
            Sample(50, 500, arrivalMs: 2000);

            Assert.Null(Snapshot.GenerationRate);
            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
        }

        [Fact]
        public void RunStarted_ResetsTheRing_AndTheLiveRate()
        {
            Publish(new RunStarted(), Request(1));
            Sample(90, 1000, arrivalMs: 1000);
            Publish(Completed(90, 1000), new RunEnded());

            Publish(new RunStarted());

            // The last run's chart must not linger beside the run that replaced it.
            Assert.Null(Snapshot.GenerationRate);
            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
        }

        [Fact]
        public void ANewRunsRing_ChartsOnlyItsOwnGeneration()
        {
            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 100);    // the baseline
            Sample(20, 200, arrivalMs: 300);    // bucket 0 — the previous run's only chart
            Publish(Completed(20, 200), new RunEnded());

            Publish(new RunStarted(), Request(1));
            Sample(10, 100, arrivalMs: 600);    // the new run's baseline
            Sample(60, 200, arrivalMs: 700);    // bucket 1 — +50 in 100ms

            var ring = Snapshot.GenerationRateHistory;
            Assert.Equal(500, ring[19]!.Value, 3);
            Assert.All(ring.Take(19), Assert.Null);
        }

        [Fact]
        public void BeforeAnyRun_TheRingIsAReservedBoxOfAbsentBuckets()
        {
            Assert.Null(Snapshot.GenerationRate);
            Assert.Equal(20, Snapshot.GenerationRateHistory.Count);
            Assert.All(Snapshot.GenerationRateHistory, Assert.Null);
        }

        // ---- The seam ----

        [Fact]
        public void TheAggregatorExposesNoChangeEvent()
        {
            // Pull, not push: per-token render amplification must be structurally impossible, not
            // merely throttled. An event here would reintroduce it.
            Assert.Empty(typeof(ThroughputAggregator).GetEvents());
        }

        [Fact]
        public void TwoReadersShareOneAggregator_AndSeeTheSameTotals()
        {
            // Cross-circuit sharing is intentional: one queue on one GPU, so two tabs should agree.
            Publish(new RunStarted(), Request(1), Completed(90, 1000));

            Assert.Equal(_aggregator.Snapshot.RunThroughput, _aggregator.Snapshot.RunThroughput);
            Assert.Equal(90, _aggregator.Snapshot.RunThroughput!.Value, 3);
        }

        [Fact]
        public void TheSnapshotIsAStableCopy_NotALiveViewOfTheRun()
        {
            Publish(new RunStarted(), Request(1), Completed(90, 1000));
            var taken = Snapshot;

            Publish(Request(2, "gemma-26b"), Completed(12, 1000));

            Assert.Single(taken.PerConfig);
            Assert.Equal(2, Snapshot.PerConfig.Count);
        }
    }
}
