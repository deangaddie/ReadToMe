using System;
using System.Diagnostics;
using System.Linq;
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
        IServiceScopeFactory scopeFactory,
        ILogger<CharacterQueueWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var item = await queue.Reader.ReadAsync(ct);
                    await ProcessItemAsync(item, ct);
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

        private async Task ProcessItemAsync(QueuedParagraph item, CancellationToken hostCt)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt, queue.ItemCancellationToken);
            var ct = linked.Token;

            try
            {
                queue.MarkProcessing(item);
                logger.LogInformation("Processing paragraph {ParagraphId} in {Folder}",
                    item.ParagraphId, item.Folder);

                var sw = Stopwatch.StartNew();

                await using var scope = scopeFactory.CreateAsyncScope();
                var attribution = scope.ServiceProvider.GetRequiredService<CharacterAttributionService>();
                var outcome = await attribution.AttributeAsync(item, ct);
                sw.Stop();

                switch (outcome.Status)
                {
                    case AttributionStatus.Resolved:
                        var reader = scope.ServiceProvider.GetRequiredService<IProjectReader>();
                        var commands = scope.ServiceProvider.GetRequiredService<IBookCommandHandler>();
                        var charId = await AssignCharacterAsync(reader, commands, item, outcome.Character!, outcome.VoiceInstructions, ct);
                        queue.MarkComplete(item, sw.Elapsed.TotalSeconds,
                            new ResolvedCharacter(charId, outcome.Character!));
                        logger.LogInformation("Completed paragraph {ParagraphId} in {Elapsed:F1}s",
                            item.ParagraphId, sw.Elapsed.TotalSeconds);
                        break;

                    case AttributionStatus.Unknown:
                        logger.LogInformation("Paragraph {ParagraphId} speaker unknown", item.ParagraphId);
                        queue.MarkUnknown(item, sw.Elapsed.TotalSeconds);
                        break;

                    case AttributionStatus.NoLlmConfigured:
                        logger.LogWarning("Paragraph {ParagraphId} failed — no LLM configured", item.ParagraphId);
                        queue.MarkFailed(item, outcome.FailureReason);
                        break;

                    case AttributionStatus.Failed:
                        logger.LogWarning("Paragraph {ParagraphId} failed: {Reason}",
                            item.ParagraphId, outcome.FailureReason);
                        queue.MarkFailed(item, outcome.FailureReason);
                        break;
                }
            }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // item-level cancel (CancelAll) — item already removed from _status by CancelAll
                logger.LogInformation("Cancelled paragraph {ParagraphId}", item.ParagraphId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed processing paragraph {ParagraphId}", item.ParagraphId);
                queue.MarkFailed(item, ex.Message);
            }
        }

        private async Task<Guid> AssignCharacterAsync(
            IProjectReader reader,
            IBookCommandHandler commands,
            QueuedParagraph item,
            string name,
            string? voiceInstructions,
            CancellationToken ct)
        {
            var characters = await reader.GetCharactersAsync(item.Folder);
            var existing = characters.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            Guid charId;
            if (existing != null)
            {
                charId = existing.Id;
            }
            else
            {
                var created = await commands.ExecuteAsync(new CreateCharacterCommand(item.Folder, name), ct);
                charId = created ?? throw new InvalidOperationException(
                    $"CreateCharacterCommand returned null for name '{name}'");
            }

            await commands.ExecuteAsync(
                new SetParagraphCharacterCommand(item.Folder, item.ParagraphId, charId, voiceInstructions), ct);

            return charId;
        }
    }
}
