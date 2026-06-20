using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Read2Me.Services.Audio;

namespace Read2Me.App.Audio
{
    public sealed class AudioQueueWorker(
        AudioQueueService queue,
        IServiceScopeFactory scopeFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var item = await queue.Reader.ReadAsync(ct);

                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<IAudioQueueProcessor>();
                    await processor.ProcessItemAsync(item, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    // CancelAll replaced the channel — loop to pick up new Reader
                }
                catch (Exception)
                {
                    // Per-item failure — continue processing next item
                }
            }
        }
    }
}
