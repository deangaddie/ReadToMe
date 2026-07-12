using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
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
            Name = "Local",
            BaseUrl = "http://localhost:8080",
        };

        private readonly FakeAiServiceReporter _reporter = new();
        private readonly EventBroadcaster<LlmStreamEvent> _broadcaster = new();
        private readonly List<LlmStreamEvent> _events = [];

        private LlmCompletionRunner Runner(ChunkedLlmClient llm)
        {
            _broadcaster.Event += _events.Add;
            return new LlmCompletionRunner(llm, _reporter, _broadcaster,
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

        // ---- Broadcast lifecycle + health streak, happy path ----

        [Fact]
        public async Task CompletedRun_BroadcastsStartDeltasCompleted_AndReportsSuccess()
        {
            var llm = new ChunkedLlmClient().Thinking("hmm").Content("{\"a\":", "1}");
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
                },
                e => Assert.Equal("hmm", Assert.IsType<ThinkingDelta>(e).Text),
                e => Assert.Equal("{\"a\":", Assert.IsType<ContentDelta>(e).Text),
                e => Assert.Equal("1}", Assert.IsType<ContentDelta>(e).Text),
                e =>
                {
                    var completed = Assert.IsType<StreamCompleted>(e);
                    // "the prompt" = 10 chars -> 3 tokens; output estimated per chunk: 5 + 2 chars -> 2 + 1
                    Assert.Equal(3, completed.TokensIn);
                    Assert.Equal(3, completed.TokensOut);
                });

            Assert.Equal(["http://localhost:8080"], _reporter.Successes);
            Assert.Empty(_reporter.Failures);
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
