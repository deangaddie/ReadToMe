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
                source, scopeFactory, NullLogger<QueueWorker<int>>.Instance);
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
                source, scopeFactory, NullLogger<QueueWorker<int>>.Instance);
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
                swappableSource, scopeFactory, NullLogger<QueueWorker<int>>.Instance);
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
