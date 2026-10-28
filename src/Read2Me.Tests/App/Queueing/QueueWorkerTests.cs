using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Queueing;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.App.Queueing
{
    public class QueueWorkerTests
    {
        [Fact]
        public async Task Worker_ContinuesAfterPerItemFailure()
        {
            var channel = Channel.CreateUnbounded<int>();
            var source = new FakeSource(channel.Reader);
            var processed = new List<int>();
            var tcs = new TaskCompletionSource();

            var processor = new FakeProcessor(i =>
            {
                processed.Add(i);
                if (i == 1) throw new InvalidOperationException("item 1 fails");
                if (i == 2) tcs.TrySetResult();
            });

            var services = new ServiceCollection();
            services.AddSingleton<IQueueProcessor<int>>(processor);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var worker = new QueueWorker<int>(
                source, new ProcessingGate<int>(), scopeFactory, NullLogger<QueueWorker<int>>.Instance);
            using var cts = new CancellationTokenSource();

            channel.Writer.TryWrite(1);
            channel.Writer.TryWrite(2);

            await worker.StartAsync(cts.Token);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(tcs.Task, completed);
            Assert.Equal(2, processed.Count);
        }

        [Fact]
        public async Task Worker_BreaksOnHostCancellation()
        {
            var channel = Channel.CreateUnbounded<int>();
            var source = new FakeSource(channel.Reader);
            var processor = new FakeProcessor(_ => { });

            var services = new ServiceCollection();
            services.AddSingleton<IQueueProcessor<int>>(processor);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var worker = new QueueWorker<int>(
                source, new ProcessingGate<int>(), scopeFactory, NullLogger<QueueWorker<int>>.Instance);
            using var cts = new CancellationTokenSource();

            await worker.StartAsync(cts.Token);
            cts.Cancel();
            // StopAsync should complete without hanging
            await worker.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Worker_SurvivesChannelClosedException()
        {
            // First channel closes, source swaps to second; worker should pick up from second.
            var first = Channel.CreateUnbounded<int>();
            var second = Channel.CreateUnbounded<int>();
            var swappableSource = new SwappableSource(first.Reader, second.Reader);
            var tcs = new TaskCompletionSource();

            var processor = new FakeProcessor(i =>
            {
                if (i == 99) tcs.TrySetResult();
            });

            var services = new ServiceCollection();
            services.AddSingleton<IQueueProcessor<int>>(processor);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var worker = new QueueWorker<int>(
                swappableSource, new ProcessingGate<int>(), scopeFactory, NullLogger<QueueWorker<int>>.Instance);
            using var cts = new CancellationTokenSource();

            second.Writer.TryWrite(99);
            await worker.StartAsync(cts.Token);

            // Closing first channel triggers ChannelClosedException; worker loops and reads from second
            first.Writer.TryComplete();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(tcs.Task, completed);
        }

        [Fact]
        public async Task Worker_DoesNotProcessWhileGateClosed_ResumesOnOpen()
        {
            var channel = Channel.CreateUnbounded<int>();
            var source = new FakeSource(channel.Reader);
            var processed = new List<int>();
            var tcs = new TaskCompletionSource();

            var processor = new FakeProcessor(i =>
            {
                processed.Add(i);
                tcs.TrySetResult();
            });

            var services = new ServiceCollection();
            services.AddSingleton<IQueueProcessor<int>>(processor);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var gate = new ProcessingGate<int>();
            gate.Close(GateOwners.WatchdogFor("llama"), "paused");

            var worker = new QueueWorker<int>(
                source, gate, scopeFactory, NullLogger<QueueWorker<int>>.Instance);
            using var cts = new CancellationTokenSource();

            channel.Writer.TryWrite(7);
            await worker.StartAsync(cts.Token);

            // Gate closed: item must not be processed.
            var early = await Task.WhenAny(tcs.Task, Task.Delay(300));
            Assert.NotEqual(tcs.Task, early);
            Assert.Empty(processed);

            // Open: worker resumes and processes the queued item.
            gate.Open(GateOwners.WatchdogFor("llama"));
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));

            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(tcs.Task, completed);
            Assert.Equal([7], processed);
        }

        [Fact]
        public async Task Gate_StartsOpen_WaitCompletesImmediately()
        {
            var gate = new ProcessingGate<int>();
            Assert.True(gate.IsOpen);
            Assert.Null(gate.CloseReason);
            await gate.WaitAsync(CancellationToken.None); // completes synchronously
        }

        [Fact]
        public async Task Gate_Close_BlocksWait_ExposesReason()
        {
            var gate = new ProcessingGate<int>();
            gate.Close(GateOwners.WatchdogFor("llama"), "recovering");

            Assert.False(gate.IsOpen);
            Assert.Equal("recovering", gate.CloseReason);

            var wait = gate.WaitAsync(CancellationToken.None);
            var raced = await Task.WhenAny(wait, Task.Delay(200));
            Assert.NotEqual(wait, raced); // still blocked
        }

        [Fact]
        public async Task Gate_Open_ReleasesPendingWait_AndSubsequentWaitImmediate()
        {
            var gate = new ProcessingGate<int>();
            gate.Close(GateOwners.WatchdogFor("llama"), "x");
            var pending = gate.WaitAsync(CancellationToken.None);

            gate.Open(GateOwners.WatchdogFor("llama"));

            await pending.WaitAsync(TimeSpan.FromSeconds(5)); // released
            Assert.True(gate.IsOpen);
            Assert.Null(gate.CloseReason);
            await gate.WaitAsync(CancellationToken.None); // subsequent wait immediate
        }

        [Fact]
        public async Task Gate_CloseTwiceThenOpenOnce_IsOpen()
        {
            var gate = new ProcessingGate<int>();
            gate.Close(GateOwners.WatchdogFor("llama"), "first");
            gate.Close(GateOwners.WatchdogFor("llama"), "second"); // idempotent, no counting
            gate.Open(GateOwners.WatchdogFor("llama"));

            Assert.True(gate.IsOpen);
            await gate.WaitAsync(CancellationToken.None);
        }

        [Fact]
        public void Gate_ClosedByTwoOwners_ReasonNamesBoth_InCloseOrder()
        {
            var gate = new ProcessingGate<int>();
            gate.Close("setup-check", "Setup check running");
            gate.Close(GateOwners.WatchdogFor("llama"), "Recovering llama: timeout");

            Assert.Equal("Setup check running; Recovering llama: timeout", gate.CloseReason);
        }

        [Fact]
        public void Gate_OwnerOpensWithoutHolding_LeavesOtherOwnersHold()
        {
            var gate = new ProcessingGate<int>();
            gate.Close("setup-check", "Setup check running");

            gate.Open(GateOwners.WatchdogFor("llama"));

            Assert.False(gate.IsOpen);
            Assert.Equal("Setup check running", gate.CloseReason);
        }

        [Fact]
        public async Task Gate_ClosedByTwoOwners_StaysClosedUntilBothOpen()
        {
            var gate = new ProcessingGate<int>();
            gate.Close(GateOwners.WatchdogFor("llama"), "Recovering llama: timeout");
            gate.Close("setup-check", "Setup check running");
            var pending = gate.WaitAsync(CancellationToken.None);

            gate.Open(GateOwners.WatchdogFor("llama"));

            Assert.False(gate.IsOpen);
            Assert.Equal("Setup check running", gate.CloseReason);
            var raced = await Task.WhenAny(pending, Task.Delay(200));
            Assert.NotEqual(pending, raced); // still blocked

            gate.Open("setup-check");

            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(gate.IsOpen);
            Assert.Null(gate.CloseReason);
        }

        [Fact]
        public void Gate_ReplaceReason_SwapsOwnersReason_WithoutOpening()
        {
            var gate = new ProcessingGate<int>();
            gate.Close("setup-check", "Setup check running");
            gate.Close(GateOwners.WatchdogFor("llama"), "Recovering llama: timeout");
            var pending = gate.WaitAsync(CancellationToken.None);

            gate.ReplaceReason(GateOwners.WatchdogFor("llama"), "llama is down: warm-up failed");

            Assert.False(gate.IsOpen);
            Assert.Equal("Setup check running; llama is down: warm-up failed", gate.CloseReason);
            gate.Open("setup-check");
            Assert.False(gate.IsOpen); // the replaced hold is still held
            Assert.False(pending.IsCompleted); // never released
        }

        [Fact]
        public void Gate_WatchdogOwner_IgnoresServiceNameCase()
        {
            var gate = new ProcessingGate<int>();
            gate.Close(GateOwners.WatchdogFor("whisper"), "Recovering whisper: timeout");

            gate.Open(GateOwners.WatchdogFor("Whisper"));

            Assert.True(gate.IsOpen);
        }

        [Fact]
        public void Gate_ReplaceReason_ForOwnerNotHolding_ClosesUnderThatOwner()
        {
            var gate = new ProcessingGate<int>();

            gate.ReplaceReason(GateOwners.WatchdogFor("llama"), "llama is down: warm-up failed");

            Assert.False(gate.IsOpen);
            Assert.Equal("llama is down: warm-up failed", gate.CloseReason);
        }

        [Fact]
        public async Task Gate_WaitAsync_HonoursCancellationWhileClosed()
        {
            var gate = new ProcessingGate<int>();
            gate.Close(GateOwners.WatchdogFor("llama"), "paused");
            using var cts = new CancellationTokenSource();

            var wait = gate.WaitAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }

        private sealed class FakeSource(ChannelReader<int> reader) : IQueueSource<int>
        {
            public ChannelReader<int> Reader => reader;
        }

        private sealed class SwappableSource(ChannelReader<int> first, ChannelReader<int> second) : IQueueSource<int>
        {
            private bool _swapped;

            public ChannelReader<int> Reader
            {
                get
                {
                    if (_swapped) return second;
                    _swapped = true;
                    return first;
                }
            }
        }

        private sealed class FakeProcessor(Action<int> handle) : IQueueProcessor<int>
        {
            public Task ProcessItemAsync(int item, CancellationToken ct)
            {
                handle(item);
                return Task.CompletedTask;
            }
        }
    }
}
