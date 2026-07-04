using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Queueing;

public sealed class QueueWorker<TItem>(
    IQueueSource<TItem> source,
    IProcessingGate<TItem> gate,
    IServiceScopeFactory scopeFactory,
    ILogger<QueueWorker<TItem>> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await gate.WaitAsync(ct);
                var item = await source.Reader.ReadAsync(ct);
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IQueueProcessor<TItem>>();
                await processor.ProcessItemAsync(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ChannelClosedException) { /* source replaced channel — loop picks up new Reader */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queue item processing failed; continuing. ({Item})", typeof(TItem).Name);
            }
        }
    }
}
