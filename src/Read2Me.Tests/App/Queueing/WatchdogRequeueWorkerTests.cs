using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Queueing;
using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.App.Queueing
{
    /// <summary>
    /// End-to-end at the worker level: a requeued item waits on the gate closed by "recovery" and is
    /// re-driven — and completes — once the gate reopens. Uses fakes only; no docker, HTTP, or delays.
    /// </summary>
    public class WatchdogRequeueWorkerTests
    {
        [Fact]
        public async Task RequeuedItem_WaitsOnClosedGate_ThenCompletesWhenReopened()
        {
            var queue = new CharacterQueueService();
            var gate = new ProcessingGate<QueuedParagraph>();
            var completed = new TaskCompletionSource();
            var attempts = new List<bool>(); // Requeued flag observed per processing attempt

            var processor = new FakeProcessor((item, _) =>
            {
                attempts.Add(item.Requeued);
                if (!item.Requeued)
                {
                    // Simulate the ServiceUnavailable path: requeue the item and close the gate as
                    // recovery would, so the requeued item must wait.
                    queue.Requeue(item);
                    gate.Close("recovering");
                }
                else
                {
                    completed.TrySetResult();
                }
                return Task.CompletedTask;
            });

            var services = new ServiceCollection();
            services.AddSingleton<IQueueProcessor<QueuedParagraph>>(processor);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var worker = new QueueWorker<QueuedParagraph>(
                queue, gate, scopeFactory, NullLogger<QueueWorker<QueuedParagraph>>.Instance);
            using var cts = new CancellationTokenSource();

            var item = new QueuedParagraph(
                new ProjectFolderId("test"), Guid.NewGuid(), "p",
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            queue.Enqueue(new[] { item });

            await worker.StartAsync(cts.Token);

            // First attempt requeues + closes the gate; the requeued item must not complete yet.
            var early = await Task.WhenAny(completed.Task, Task.Delay(300));
            Assert.NotEqual(completed.Task, early);

            gate.Open();

            var done = await Task.WhenAny(completed.Task, Task.Delay(5000));
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(completed.Task, done);
            Assert.Equal(new[] { false, true }, attempts);
        }

        private sealed class FakeProcessor(Func<QueuedParagraph, CancellationToken, Task> handle)
            : IQueueProcessor<QueuedParagraph>
        {
            public Task ProcessItemAsync(QueuedParagraph item, CancellationToken ct) => handle(item, ct);
        }
    }
}
