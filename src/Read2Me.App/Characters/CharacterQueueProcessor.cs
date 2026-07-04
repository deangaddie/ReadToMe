using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Characters;

namespace Read2Me.App.Characters
{
    public sealed class CharacterQueueProcessor(
        CharacterQueueService queue,
        CharacterAttributionService attribution,
        CharacterResolver resolver,
        IBookCommandHandler commands,
        ILogger<CharacterQueueProcessor> logger) : ICharacterQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedParagraph item, CancellationToken hostCt)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt, queue.ItemCancellationToken);
            var ct = linked.Token;

            try
            {
                queue.MarkProcessing(item);
                logger.LogInformation("Processing paragraph {ParagraphId} in {Folder}",
                    item.ParagraphId, item.Folder);

                var sw = Stopwatch.StartNew();
                var outcome = await attribution.AttributeAsync(item, ct);
                sw.Stop();

                switch (outcome.Status)
                {
                    case AttributionStatus.Resolved:
                        var charId = await AssignCharacterAsync(item, outcome.Character!, outcome.VoiceInstructions, ct);
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

                    case AttributionStatus.ServiceUnavailable:
                        // Watchdog is recovering the service. Requeue once so recovery is invisible in
                        // the results; a second outage for the same item (service down) fails it.
                        if (item.Requeued)
                        {
                            logger.LogWarning("Paragraph {ParagraphId} service unavailable again after requeue — failing: {Reason}",
                                item.ParagraphId, outcome.FailureReason);
                            queue.MarkFailed(item, outcome.FailureReason);
                        }
                        else
                        {
                            logger.LogInformation("Paragraph {ParagraphId} service unavailable — requeuing: {Reason}",
                                item.ParagraphId, outcome.FailureReason);
                            queue.Requeue(item);
                        }
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
            QueuedParagraph item,
            string name,
            string? voiceInstructions,
            CancellationToken ct)
        {
            var charId = await resolver.ResolveOrCreateAsync(item.Folder, name, ct);
            await commands.ExecuteAsync(
                new SetParagraphCharacterCommand(item.Folder, item.ParagraphId, charId, voiceInstructions), ct);
            return charId;
        }
    }
}
