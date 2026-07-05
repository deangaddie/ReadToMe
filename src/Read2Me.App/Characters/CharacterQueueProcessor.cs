using System;
using System.Collections.Generic;
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
        LlmSettingsService settings,
        ILogger<CharacterQueueProcessor> logger) : ICharacterQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedParagraph item, CancellationToken hostCt)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt, queue.ItemCancellationToken);
            var ct = linked.Token;

            // Items drained into the batch that have not received an outcome yet — failed
            // wholesale if processing throws, so no drained item is silently dropped.
            var pending = new List<QueuedParagraph> { item };

            try
            {
                var batchSize = (await settings.GetActiveConfigAsync())?.AttributionBatchSize ?? 1;
                IReadOnlyList<QueuedParagraph> batch = queue.DrainBatch(item, Math.Max(1, batchSize));
                pending = new List<QueuedParagraph>(batch);
                foreach (var b in batch)
                    queue.MarkProcessing(b);

                logger.LogInformation("Processing {Count} paragraph(s) starting at {ParagraphId} in {Folder}",
                    batch.Count, item.ParagraphId, item.Folder);

                while (batch.Count > 0)
                {
                    var sw = Stopwatch.StartNew();
                    var result = await attribution.AttributeBatchAsync(batch, ct);
                    sw.Stop();

                    var perItemSeconds = result.Outcomes.Count > 0
                        ? sw.Elapsed.TotalSeconds / result.Outcomes.Count
                        : sw.Elapsed.TotalSeconds;

                    foreach (var (batchItem, outcome) in result.Outcomes)
                    {
                        await ApplyOutcomeAsync(batchItem, outcome, perItemSeconds, ct);
                        pending.Remove(batchItem);
                    }

                    // Items trimmed off the contiguous run go around again as a fresh batch.
                    batch = result.Deferred;
                }
            }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // item-level cancel (CancelAll) — items already removed from _status by CancelAll
                logger.LogInformation("Cancelled paragraph {ParagraphId}", item.ParagraphId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed processing paragraph {ParagraphId}", item.ParagraphId);
                foreach (var p in pending)
                    queue.MarkFailed(p, ex.Message);
            }
        }

        private async Task ApplyOutcomeAsync(
            QueuedParagraph item, AttributionOutcome outcome, double elapsedSeconds, CancellationToken ct)
        {
            switch (outcome.Status)
            {
                case AttributionStatus.Resolved:
                    var charId = await AssignCharacterAsync(item, outcome.Character!, outcome.VoiceInstructions, ct);
                    queue.MarkComplete(item, elapsedSeconds,
                        new ResolvedCharacter(charId, outcome.Character!));
                    logger.LogInformation("Completed paragraph {ParagraphId} in {Elapsed:F1}s",
                        item.ParagraphId, elapsedSeconds);
                    break;

                case AttributionStatus.Unknown:
                    logger.LogInformation("Paragraph {ParagraphId} speaker unknown", item.ParagraphId);
                    queue.MarkUnknown(item, elapsedSeconds);
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
