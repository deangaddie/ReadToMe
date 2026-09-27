using Read2Me.Services.Audio;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Events;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioCppGateTests
    {
        private static (AudioCppGate Gate, FakeAudioCppHandler Handler, List<AudioGenEvent> Events) Build(string? loaded)
        {
            var handler = new FakeAudioCppHandler { LoadedModel = loaded };
            var broadcaster = new EventBroadcaster<AudioGenEvent>();
            var events = new List<AudioGenEvent>();
            broadcaster.Event += events.Add;
            return (new AudioCppGate(new SingleHandlerHttpClientFactory(handler), broadcaster), handler, events);
        }

        [Fact]
        public async Task Checks_the_loaded_model_before_sending()
        {
            var (gate, handler, events) = Build(loaded: "breeze-q8");

            var result = await gate.RunAsync("http://acpp", "breeze-q8", _ =>
            {
                handler.Paths.Add("send");
                return Task.FromResult(42);
            }, CancellationToken.None);

            Assert.Equal(42, result);
            Assert.Equal(["GET /v1/models", "send"], handler.Paths);
            Assert.Empty(events);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("voxcpm2")]
        public async Task A_model_that_is_not_loaded_publishes_a_loading_status_then_sends(string? loaded)
        {
            var (gate, handler, events) = Build(loaded);
            var sent = false;

            await gate.RunAsync("http://acpp", "breeze-q8", _ =>
            {
                sent = true;
                return Task.FromResult(0);
            }, CancellationToken.None);

            Assert.True(sent);
            Assert.Equal(new TtsModelLoading("breeze-q8"), Assert.Single(events));
        }

        /// <summary>A model audio.cpp doesn't list (a mistyped ModelId) is not "loading" — audio.cpp rejects it.</summary>
        [Fact]
        public async Task A_model_the_server_does_not_list_publishes_no_loading_status()
        {
            var (gate, _, events) = Build(loaded: "breeze-q8");

            await gate.RunAsync("http://acpp", "breeze-typo", _ => Task.FromResult(0), CancellationToken.None);

            Assert.Empty(events);
        }

        [Fact]
        public async Task Requests_to_one_endpoint_run_one_at_a_time()
        {
            var (gate, _, _) = Build(loaded: "breeze-q8");
            var inFlight = 0;
            var maxInFlight = 0;

            async Task<int> Send(CancellationToken ct)
            {
                var now = Interlocked.Increment(ref inFlight);
                lock (gate) maxInFlight = Math.Max(maxInFlight, now);
                await Task.Delay(20, ct);
                Interlocked.Decrement(ref inFlight);
                return 0;
            }

            await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => gate.RunAsync("http://acpp", "breeze-q8", Send, CancellationToken.None)));

            Assert.Equal(1, maxInFlight);
        }

        [Fact]
        public async Task Different_endpoints_do_not_block_each_other()
        {
            var (gate, _, _) = Build(loaded: "breeze-q8");
            var release = new TaskCompletionSource<int>();

            var first = gate.RunAsync("http://acpp-a", "breeze-q8", _ => release.Task, CancellationToken.None);
            var second = await gate.RunAsync("http://acpp-b", "breeze-q8", _ => Task.FromResult(7), CancellationToken.None);

            Assert.Equal(7, second);
            Assert.False(first.IsCompleted);
            release.SetResult(0);
            await first;
        }

        [Fact]
        public async Task A_failed_send_releases_the_lock()
        {
            var (gate, _, _) = Build(loaded: "breeze-q8");

            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync<int>(
                "http://acpp", "breeze-q8", _ => throw new InvalidOperationException(), CancellationToken.None));

            var result = await gate.RunAsync("http://acpp", "breeze-q8", _ => Task.FromResult(1), CancellationToken.None);
            Assert.Equal(1, result);
        }
    }
}
