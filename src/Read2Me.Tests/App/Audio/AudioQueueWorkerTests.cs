using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Audio;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App.Audio
{
    public class AudioQueueWorkerTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        [Fact]
        public async Task Worker_ContinuesAfterPerItemFailure()
        {
            var queue = new AudioQueueService();
            var processed = new List<Guid>();
            var tcs = new TaskCompletionSource();

            var processor = new FakeProcessor(item =>
            {
                processed.Add(item.Item.ParagraphItemId);
                if (processed.Count == 1)
                    throw new InvalidOperationException("first item fails");
                if (processed.Count == 2)
                    tcs.TrySetResult();
            });

            var services = new ServiceCollection();
            services.AddSingleton<IAudioQueueProcessor>(processor);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var worker = new AudioQueueWorker(queue, scopeFactory);
            using var cts = new CancellationTokenSource();

            var item1 = new AudioItemRef(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var item2 = new AudioItemRef(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            queue.Enqueue(Folder, [item1, item2]);

            var workerTask = worker.StartAsync(cts.Token);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(tcs.Task, completed);
            Assert.Equal(2, processed.Count);
        }

        private sealed class FakeProcessor(Action<QueuedAudioItem> handle) : IAudioQueueProcessor
        {
            public Task ProcessItemAsync(QueuedAudioItem item, CancellationToken ct)
            {
                handle(item);
                return Task.CompletedTask;
            }
        }
    }
}
