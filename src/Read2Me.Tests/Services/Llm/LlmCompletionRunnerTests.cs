using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Exceptions;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class LlmCompletionRunnerTests
    {
        private static LlmServerConfig Config() => new()
        {
            Id = 7,
            Name = "Local",
            BaseUrl = "http://localhost:8080",
        };

        private readonly FakeAiServiceReporter _reporter = new();
        private readonly EventBroadcaster<LlmStreamEvent> _broadcaster = new();
        private readonly EventBroadcaster<LlmTimingsSample> _samples = new();
        private readonly List<LlmStreamEvent> _events = [];
        private readonly List<LlmTimingsSample> _sampled = [];

        private LlmCompletionRunner Runner(ChunkedLlmClient llm)
        {
            _broadcaster.Event += _events.Add;
            _samples.Event += _sampled.Add;
            return new LlmCompletionRunner(llm, _reporter, _broadcaster, _samples,
                NullLogger<LlmCompletionRunner>.Instance);
        }

        // ---- Raw overload (None shape) ----

        [Fact]
        public async Task RawOverload_ReadsToEnd_ReturnsCompletedWithRawAsValue()
        {
            var llm = new ChunkedLlmClient().Content("Hello ", "world");
            var runner = Runner(llm);

            var result = await runner.RunAsync(
                new LlmRunRequest(Config(), "prompt", "Label", Shape: CompletionShape.None),
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.Completed, result.Outcome);
            Assert.Equal("Hello world", result.Value);
            Assert.Equal("Hello world", result.Raw);
            Assert.Equal(2, llm.ChunksPulled);
        }

        [Fact]
        public async Task DisableThinking_PassesThroughToClient()
        {
            var llm = new ChunkedLlmClient().Content("ok");
            var runner = Runner(llm);

            await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L", Shape: CompletionShape.None, DisableThinking: true),
                CancellationToken.None);
            await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L", Shape: CompletionShape.None),
                CancellationToken.None);

            Assert.True(llm.Calls[0].DisableThinking);
            Assert.False(llm.Calls[1].DisableThinking);
        }

        // ---- Broadcast lifecycle + health streak, happy path ----

        [Fact]
        public async Task CompletedRun_BroadcastsStartDeltasCompleted_AndReportsSuccess()
        {
            // With timings_per_token on, metrics arrive interleaved mid-stream — so a run the
            // completion scanner stops early still has the server's last reading to report.
            var llm = new ChunkedLlmClient().Thinking("hmm").Content("{\"a\":")
                .Metrics(
                    new LlmTimings(CacheN: null, PromptN: null, PromptMs: null,
                        PredictedN: 24, PredictedMs: 400),
                    new LlmUsage(PromptTokens: 17, CompletionTokens: 24, TotalTokens: 41, CachedTokens: null))
                .Content("1}");
            var runner = Runner(llm);

            await runner.RunAsync(
                new LlmRunRequest(Config(), "the prompt", "My label"),
                CancellationToken.None);

            Assert.Collection(_events,
                e =>
                {
                    var started = Assert.IsType<RequestStarted>(e);
                    Assert.Equal("My label", started.ParagraphPreview);
                    Assert.Equal("the prompt", started.Prompt);
                    // Config rides request start, keyed by id — the aggregator latches it here
                    // and attributes the StreamCompleted that follows to that config.
                    Assert.Equal(7, started.ConfigId);
                    Assert.Equal("Local", started.ConfigName);
                },
                e => Assert.Equal("hmm", Assert.IsType<ThinkingDelta>(e).Text),
                e => Assert.Equal("{\"a\":", Assert.IsType<ContentDelta>(e).Text),
                e => Assert.Equal("1}", Assert.IsType<ContentDelta>(e).Text),
                e =>
                {
                    // Every figure is the server's own: prompt tokens from usage, generation from
                    // timings, and the rate recomputed as predicted_n ÷ predicted_ms.
                    var completed = Assert.IsType<StreamCompleted>(e);
                    Assert.Equal(17, completed.TokensIn);
                    Assert.Equal(24, completed.TokensOut);
                    Assert.Equal(400, completed.GenerationMs);
                    Assert.Equal(60.0, completed.TokensPerSecond!.Value, 6);
                });

            Assert.Equal(["http://localhost:8080"], _reporter.Successes);
            Assert.Empty(_reporter.Failures);
        }

        // ---- The live reading feed ----

        [Fact]
        public async Task EveryTimingsCarryingChunk_IsPublishedAsASample_ForTheLiveFigures()
        {
            var llm = new ChunkedLlmClient()
                .Metrics(new LlmTimings(null, null, null, PredictedN: 10, PredictedMs: 100))
                .Content("{")
                .Metrics(new LlmTimings(null, null, null, PredictedN: 20, PredictedMs: 200))
                .Content("}");
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            Assert.Collection(_sampled,
                s => Assert.Equal(10, s.Timings.PredictedN),
                s => Assert.Equal(20, s.Timings.PredictedN));
        }

        [Fact]
        public async Task SampleArrivalStamps_AreMonotonic_AndComeFromTheProducer()
        {
            // The consumers of a reading are clock-free by design, so the stamp is this class's
            // job. It must not go backwards, or a ring bucketed on it would chart the past.
            var llm = new ChunkedLlmClient()
                .Metrics(new LlmTimings(null, null, null, 10, 100))
                .Metrics(new LlmTimings(null, null, null, 20, 200));
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            Assert.Equal(2, _sampled.Count);
            Assert.True(_sampled[1].Arrival >= _sampled[0].Arrival);
            Assert.True(_sampled[0].Arrival > TimeSpan.Zero);
        }

        [Fact]
        public async Task SamplesStayOffTheStreamEventBus_WhereEverySubscriberRepaints()
        {
            // One of these rides every token. On the LlmStreamEvent family it would cost a SignalR
            // repaint per token per circuit and be journalled for replay.
            var llm = new ChunkedLlmClient()
                .Metrics(new LlmTimings(null, null, null, 10, 100))
                .Content("{}");
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            // A sample is not an LlmStreamEvent at all — the separation is a type, not a filter a
            // subscriber has to remember. So the stream bus can only carry the turn's own events.
            Assert.Single(_sampled);
            Assert.Collection(_events,
                e => Assert.IsType<RequestStarted>(e),
                e => Assert.IsType<ContentDelta>(e),
                e => Assert.IsType<StreamCompleted>(e));
        }

        [Fact]
        public async Task AChunkWithNoTimings_PublishesNoSample()
        {
            // Absence is not a reading of zero — a backend that measures nothing feeds nothing.
            var llm = new ChunkedLlmClient().Content("{", "}");
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            Assert.Empty(_sampled);
        }

        [Fact]
        public async Task BackendSendsNoTimings_StreamCompletedCarriesNullsNotZeros()
        {
            // Nothing is estimated any more, so a backend that measures nothing reports nothing.
            // A "0 tok/s" here would claim the model generated nothing.
            var llm = new ChunkedLlmClient().Content("{}");
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            var completed = Assert.IsType<StreamCompleted>(_events.Last());
            Assert.Null(completed.TokensIn);
            Assert.Null(completed.TokensOut);
            Assert.Null(completed.GenerationMs);
            Assert.Null(completed.TokensPerSecond);
        }

        // ---- Completion scanner stop ----

        [Fact]
        public async Task ObjectShape_StopsPullingTheMomentTheObjectCloses()
        {
            var llm = new ChunkedLlmClient()
                .Content("{\"a\": 1", "}", "trailing thinking", "more");
            var runner = Runner(llm);

            var result = await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L", Shape: CompletionShape.Object),
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.Completed, result.Outcome);
            Assert.Equal("{\"a\": 1}", result.Raw);
            Assert.Equal(2, llm.ChunksPulled);
        }

        [Fact]
        public async Task ArrayShape_StopsPullingTheMomentTheArrayCloses()
        {
            var llm = new ChunkedLlmClient()
                .Content("[1, 2", "]", "extra");
            var runner = Runner(llm);

            var result = await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L", Shape: CompletionShape.Array),
                CancellationToken.None);

            Assert.Equal("[1, 2]", result.Raw);
            Assert.Equal(2, llm.ChunksPulled);
        }

        // ---- Aborts: the last reading must survive the death of the request ----

        /// <summary>
        /// Scripts a request that generated 24 tokens in 400ms and then died on the next pull.
        /// The reading arrives mid-stream via <c>timings_per_token</c> — an aborted stream sends no
        /// final chunk, so that interleaved reading is the only record there will ever be.
        /// </summary>
        private static ChunkedLlmClient DiesAfterGenerating(Exception ex) =>
            new ChunkedLlmClient()
                .Metrics(new LlmTimings(CacheN: null, PromptN: null, PromptMs: null,
                    PredictedN: 24, PredictedMs: 400))
                .Content("{\"par")
                .Throws(ex);

        [Fact]
        public async Task CancelledMidStream_PublishesItsLastReading_SoTheWorkStillCountsTowardTheRunTotal()
        {
            // The user stopped an in-flight send. It really generated 24 tokens; without this the
            // run total under-counts in the direction that looks plausible.
            var llm = DiesAfterGenerating(new OperationCanceledException("stopped"));
            var runner = Runner(llm);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), cts.Token));

            var aborted = Assert.IsType<StreamAborted>(_events[^1]);
            Assert.Equal(24, aborted.TokensOut);
            Assert.Equal(400, aborted.GenerationMs);
            Assert.Equal(60.0, aborted.TokensPerSecond!.Value, 6);
        }

        [Fact]
        public async Task CancelWrappedInAnotherExceptionType_StillPublishesItsLastReading()
        {
            // The other cancel path: a client that surfaced the cancel as its own exception type.
            // It is still a cancel, and the tokens it generated are still real.
            var llm = DiesAfterGenerating(new InvalidOperationException("wrapped cancel"));
            _reporter.Managed = true;
            var runner = Runner(llm);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), cts.Token));

            Assert.Equal(24, Assert.IsType<StreamAborted>(_events[^1]).TokensOut);
        }

        [Fact]
        public async Task ACancelPublishesNoStreamFailed_AndSurfacesNoErrorToTheUser()
        {
            // A cancel is not a service failure. StreamFailed is what LlmStreamView renders in the
            // error colour, so the measurement had to arrive on an event of its own rather than by
            // widening StreamFailed to carry it.
            var llm = DiesAfterGenerating(new OperationCanceledException("stopped"));
            _reporter.Managed = true;
            var runner = Runner(llm);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), cts.Token));

            Assert.DoesNotContain(_events, e => e is StreamFailed);
            Assert.Empty(_reporter.Failures);
        }

        [Fact]
        public async Task FailedMidStream_PublishesItsLastReading_AndStillSurfacesTheError()
        {
            // The watchdog killed a wedged container. Both things are true and neither is the
            // other's business: 24 tokens were really generated, and the user still needs the error.
            var llm = DiesAfterGenerating(new HttpRequestException("connection reset"));
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            var aborted = Assert.IsType<StreamAborted>(_events[^2]);
            Assert.Equal(24, aborted.TokensOut);
            Assert.Equal(400, aborted.GenerationMs);
            Assert.Equal("connection reset", Assert.IsType<StreamFailed>(_events[^1]).Reason);
        }

        [Fact]
        public async Task AnAbortedRequestNeverPublishesStreamCompleted()
        {
            // The stream did not complete. Reusing StreamCompleted would have needed no aggregator
            // change at all, and would have lied to every future subscriber that reads it as
            // "this request succeeded".
            var llm = DiesAfterGenerating(new HttpRequestException("down"));
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            Assert.DoesNotContain(_events, e => e is StreamCompleted);
        }

        [Fact]
        public async Task DiedBeforeAnyReadingArrived_ReportsAbsence_NeverZero()
        {
            // A request killed during model load or prompt eval measured nothing. "0 tok/s" would
            // claim it generated nothing; null says we do not know, and cannot zero a total.
            var llm = new ChunkedLlmClient().Throws(new HttpRequestException("died on connect"));
            var runner = Runner(llm);

            await runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), CancellationToken.None);

            var aborted = Assert.IsType<StreamAborted>(_events[^2]);
            Assert.Null(aborted.TokensOut);
            Assert.Null(aborted.GenerationMs);
            Assert.Null(aborted.TokensPerSecond);
        }

        // ---- Cancellation ----

        [Fact]
        public async Task Cancelled_ThrowsAndNeverReportsAFailure()
        {
            // A client that surfaces the cancel as its own exception type (as OpenAiLlmClient once did
            // by wrapping it in LlmProviderException) must still cancel, not look like a dead service:
            // a reported failure would trip watchdog recovery and escalate to the next chain config.
            var llm = new ChunkedLlmClient().Throws(new InvalidOperationException("wrapped cancel"));
            _reporter.Managed = true;
            var runner = Runner(llm);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), cts.Token));

            Assert.Empty(_reporter.Failures);
        }

        // ---- Failure mapping ----

        [Fact]
        public async Task MidStreamException_ManagedService_MapsToServiceUnavailable()
        {
            var llm = new ChunkedLlmClient()
                .Content("{\"par")
                .Throws(new HttpRequestException("connection reset"));
            _reporter.Managed = true;
            var runner = Runner(llm);

            var result = await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L"),
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.ServiceUnavailable, result.Outcome);
            Assert.Equal("connection reset", result.Error);
            Assert.Equal("connection reset",
                Assert.IsType<StreamFailed>(_events[^1]).Reason);
            Assert.Equal("http://localhost:8080", Assert.Single(_reporter.Failures).BaseUrl);
            Assert.Empty(_reporter.Successes);
        }

        [Fact]
        public async Task MidStreamException_RemoteService_MapsToFailed()
        {
            var llm = new ChunkedLlmClient().Throws(new HttpRequestException("boom"));
            var runner = Runner(llm); // Managed defaults false

            var result = await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L"),
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.Failed, result.Outcome);
            Assert.Equal("boom", result.Error);
            Assert.IsType<StreamFailed>(_events[^1]);
        }

        // ---- Model still loading (switch-and-wait) ----

        [Fact]
        public async Task ModelStillLoading_ReturnsModelLoading_WithoutReportingFailure()
        {
            // A switchable llama endpoint stayed responsive but the model had not finished loading
            // in budget. Reporting it as a failure would trip watchdog recovery (restart mid-load)
            // and escalate the chain to the next config, evicting the very model we are waiting for.
            var llm = new ChunkedLlmClient().Throws(new ModelStillLoadingException(
                "http://localhost:8080", "gemma-4b",
                TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(300)));
            _reporter.Managed = true;   // even a managed (restartable) endpoint must not be reported
            var runner = Runner(llm);

            var result = await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L"),
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.ModelLoading, result.Outcome);
            // The health monitor never sees it — no failure and no spurious success.
            Assert.Empty(_reporter.Failures);
            Assert.Empty(_reporter.Successes);
            // Not an error the user sees, so no StreamFailed; but the abort reading is still published.
            Assert.DoesNotContain(_events, e => e is StreamFailed);
            Assert.Contains(_events, e => e is StreamAborted);
        }

        // ---- Cancel vs timeout ----

        [Fact]
        public async Task GenuineCancel_ThrowsThrough()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var llm = new ChunkedLlmClient().Throws(new OperationCanceledException(cts.Token));
            var runner = Runner(llm);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync(new LlmRunRequest(Config(), "p", "L"), cts.Token));
        }

        [Fact]
        public async Task ClientTimeout_TokenNotCancelled_MapsToFailureOutcome()
        {
            // OperationCanceledException without our token cancelled = HttpClient timeout / stall.
            var llm = new ChunkedLlmClient().Throws(new OperationCanceledException("stalled"));
            var runner = Runner(llm);

            var result = await runner.RunAsync(
                new LlmRunRequest(Config(), "p", "L"),
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.Failed, result.Outcome);
            Assert.IsType<StreamFailed>(_events[^1]);
        }

        // ---- Parsed overload ----

        private static bool ParseLength(string raw, out int value, out string? error)
        {
            value = raw.Length;
            error = null;
            return true;
        }

        [Fact]
        public async Task ParsedOverload_ParserAccepts_ReturnsCompletedWithValue()
        {
            var llm = new ChunkedLlmClient().Content("{\"x\":1}");
            var runner = Runner(llm);

            var result = await runner.RunAsync<int>(
                new LlmRunRequest(Config(), "p", "L"), ParseLength,
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.Completed, result.Outcome);
            Assert.Equal(7, result.Value);
            Assert.Equal("{\"x\":1}", result.Raw);
        }

        [Fact]
        public async Task ParseFailure_TruncatesRawTo200Chars_PublishesStreamFailed()
        {
            // 300-char JSON object so the reason must clip the raw echo at 200.
            var raw = "{\"pad\":\"" + new string('x', 290) + "\"}";
            Assert.Equal(300, raw.Length);
            var llm = new ChunkedLlmClient().Content(raw);
            var runner = Runner(llm);

            static bool Reject(string raw, out int value, out string? error)
            {
                value = 0;
                error = "not what I wanted.";
                return false;
            }

            var result = await runner.RunAsync<int>(
                new LlmRunRequest(Config(), "p", "L"), Reject,
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.ParseFailed, result.Outcome);
            Assert.Equal(0, result.Value);
            Assert.Equal(raw, result.Raw);
            Assert.NotNull(result.Error);
            Assert.Contains("not what I wanted.", result.Error);
            Assert.Contains(raw[..200], result.Error);
            Assert.DoesNotContain(raw[..201], result.Error);

            var failed = Assert.IsType<StreamFailed>(_events[^1]);
            Assert.Equal(result.Error, failed.Reason);
            // Stream itself completed fine: health streak still records success.
            Assert.Equal(["http://localhost:8080"], _reporter.Successes);
        }

        [Fact]
        public async Task ParseFailure_RawShorterThan200_EchoesWholeRaw()
        {
            var llm = new ChunkedLlmClient().Content("{}");
            var runner = Runner(llm);

            static bool Reject(string raw, out int value, out string? error)
            {
                value = 0;
                error = "bad.";
                return false;
            }

            var result = await runner.RunAsync<int>(
                new LlmRunRequest(Config(), "p", "L"), Reject,
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.ParseFailed, result.Outcome);
            Assert.Contains("{}", result.Error);
        }

        [Fact]
        public async Task ParsedOverload_StreamFailure_SkipsParserAndKeepsFailureOutcome()
        {
            var llm = new ChunkedLlmClient().Throws(new HttpRequestException("down"));
            var runner = Runner(llm);
            var parserCalled = false;

            bool Spy(string raw, out int value, out string? error)
            {
                parserCalled = true;
                value = 0;
                error = null;
                return true;
            }

            var result = await runner.RunAsync<int>(
                new LlmRunRequest(Config(), "p", "L"), Spy,
                CancellationToken.None);

            Assert.Equal(LlmRunOutcome.Failed, result.Outcome);
            Assert.False(parserCalled);
        }
    }
}
