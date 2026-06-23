using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
using Read2Me.App.Queueing;
using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.App.Characters
{
    public class CharacterQueueWorkerTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        [Fact]
        public async Task Worker_ContinuesAfterPerItemFailure()
        {
            var queue = new CharacterQueueService();
            var processed = new List<Guid>();
            var tcs = new TaskCompletionSource();

            var processor = new FakeProcessor(item =>
            {
                processed.Add(item.ParagraphId);
                if (processed.Count == 1)
                    throw new InvalidOperationException("first item fails");
                if (processed.Count == 2)
                    tcs.TrySetResult();
            });

            var services = new ServiceCollection();
            services.AddSingleton<ICharacterQueueProcessor>(processor);
            services.AddScoped<IQueueProcessor<QueuedParagraph>>(
                sp => sp.GetRequiredService<ICharacterQueueProcessor>());
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var worker = new QueueWorker<QueuedParagraph>(
                queue, scopeFactory, NullLogger<QueueWorker<QueuedParagraph>>.Instance);
            using var cts = new CancellationTokenSource();

            var p1 = new QueuedParagraph(Folder, Guid.NewGuid(), "a", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var p2 = new QueuedParagraph(Folder, Guid.NewGuid(), "b", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            queue.Enqueue([p1, p2]);

            var workerTask = worker.StartAsync(cts.Token);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(tcs.Task, completed);   // 2nd item processed => worker survived the throw
            Assert.Equal(2, processed.Count);
        }

        private sealed class FakeProcessor(Action<QueuedParagraph> handle) : ICharacterQueueProcessor
        {
            public Task ProcessItemAsync(QueuedParagraph item, CancellationToken ct)
            {
                handle(item);
                return Task.CompletedTask;
            }
        }
    }
}
