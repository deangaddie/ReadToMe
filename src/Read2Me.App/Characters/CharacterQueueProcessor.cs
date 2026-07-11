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
        ILogger<CharacterQueueProcessor> logger) : ICharacterQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedParagraph item, CancellationToken hostCt)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt, queue.ItemCancellationToken);
            var ct = linked.Token;

            // Drained items that have not received an outcome yet — failed wholesale if processing
            // throws, so no drained item is silently dropped. Whittled down as the stream decides each.
            var pending = new List<QueuedParagraph> { item };

            try
            {
                // Drain the whole queue and run the primary across all of it before any escalation
                // (queue-wide escalation: one model burst per chain step, no per-item swap). The
                // drained items keep their Queued status; only the in-flight LLM chunk (batch size)
                // flips to Processing, via the ChunkStarted callback, so the tree shows the true
                // batch being worked rather than the whole queue. An item its chunk left suspect
                // drops back to Queued (ItemDeferred) — it is waiting on a later escalation step,
                // not being worked, so it must not linger on the Processing chip.
                IReadOnlyList<QueuedParagraph> queued = queue.DrainAll(item);
                pending = new List<QueuedParagraph>(queued);

                logger.LogInformation("Processing {Count} paragraph(s) starting at {ParagraphId} in {Folder}",
                    queued.Count, item.ParagraphId, item.Folder);

                void MarkChunkProcessing(IReadOnlyList<QueuedParagraph> chunk)
                {
                    foreach (var c in chunk)
                        queue.MarkProcessing(c);
                }

                var callbacks = new AttributionQueueCallbacks(
                    ChunkStarted: MarkChunkProcessing,
                    ItemDeferred: queue.MarkDeferred);

                var sw = Stopwatch.StartNew();
                await foreach (var (streamItem, outcome) in
                    attribution.AttributeQueueAsync(queued, callbacks, ct))
                {
                    var elapsed = sw.Elapsed.TotalSeconds;
                    sw.Restart();
                    await ApplyOutcomeAsync(streamItem, outcome, elapsed, ct);
                    pending.Remove(streamItem);
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
                    queue.MarkUnknown(item, elapsedSeconds, outcome.FailureReason);
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
