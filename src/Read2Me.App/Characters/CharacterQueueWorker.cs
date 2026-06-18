using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Characters;

namespace Read2Me.App.Characters
{
    public sealed class CharacterQueueWorker(
        CharacterQueueService queue,
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
                    var processor = scope.ServiceProvider.GetRequiredService<ICharacterQueueProcessor>();
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
            }
        }
    }
}
