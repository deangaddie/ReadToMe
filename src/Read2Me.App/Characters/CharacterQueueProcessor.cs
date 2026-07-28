using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;

namespace Read2Me.App.Characters
{
    internal sealed class CharacterQueueProcessor(
        CharacterQueueService queue,
        AttributionEscalationChain chain,
        CharacterResolver resolver,
        IUnattributedItemCounter reader,
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
                    chain.AttributeQueueAsync(queued, callbacks, ct))
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
                // An answer applies whether or not every speaker in it was identified: the segments
                // it *did* attribute are real work, and the paragraph stays queue-eligible while any
                // Character item is left unstamped.
                case AttributionStatus.Resolved:
                case AttributionStatus.Unknown when outcome.Segments is not null:
                    await ApplySegmentsAsync(item, outcome.Segments!, ct);
                    var unattributed = await reader.CountUnattributedCharacterItemsAsync(item.Folder, item.ParagraphId);
                    if (unattributed > 0)
                    {
                        logger.LogInformation("Paragraph {ParagraphId} has {Count} unattributed item(s) after apply",
                            item.ParagraphId, unattributed);
                        queue.MarkUnknown(item, elapsedSeconds, outcome.FailureReason);
                    }
                    else
                    {
                        queue.MarkComplete(item, elapsedSeconds);
                        logger.LogInformation("Completed paragraph {ParagraphId} in {Elapsed:F1}s",
                            item.ParagraphId, elapsedSeconds);
                    }
                    break;

                case AttributionStatus.Unknown:
                    // No segments to apply (an empty paragraph) — nothing was attributed.
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

                case AttributionStatus.ModelLoading:
                    // The target model is still loading on a switchable llama endpoint — provider
                    // busy, not dead. Requeue with exponential backoff, indefinitely: failing or
                    // escalating would evict the very load we are waiting for. This is DISTINCT from
                    // ServiceUnavailable's requeue-once-then-fail — it never touches the Requeued
                    // flag, so it never consumes that budget, and a genuinely wedged load simply
                    // loops until the user cancels.
                    var backoff = ModelLoadBackoff(item.LoadAttempts);
                    logger.LogInformation(
                        "Paragraph {ParagraphId} model still loading — requeuing in {Backoff:0.#}s (attempt {Attempt}): {Reason}",
                        item.ParagraphId, backoff.TotalSeconds, item.LoadAttempts + 1, outcome.FailureReason);
                    queue.RequeueForModelLoad(item, backoff);
                    break;
            }
        }

        /// <summary>Base delay for the first model-load retry; doubles each attempt up to the cap.</summary>
        private static readonly TimeSpan ModelLoadBackoffBase = TimeSpan.FromSeconds(2);

        /// <summary>Upper bound on the model-load retry backoff — a wedged load polls at this cadence.</summary>
        private static readonly TimeSpan ModelLoadBackoffCap = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Exponential-with-cap backoff for an indefinitely-retried model load. <paramref name="attempt"/>
        /// is the 0-based prior attempt count: 0→2s, 1→4s, 2→8s, 3→16s, 4→30s (cap), and 30s
        /// thereafter. The shift is bounded so a long-running wedged load can never overflow.
        /// </summary>
        internal static TimeSpan ModelLoadBackoff(int attempt)
        {
            if (attempt < 0) attempt = 0;
            // Cap the doubling factor at 16 (attempt ≥ 4) before it can overflow or exceed the cap.
            var factor = attempt >= 4 ? 16 : 1 << attempt;
            var delay = ModelLoadBackoffBase * factor;
            return delay < ModelLoadBackoffCap ? delay : ModelLoadBackoffCap;
        }

        /// <summary>
        /// Resolves each segment's speaker to a character id, then applies the whole list in one
        /// command. Resolution happens here, not in the handler: an unlisted name that survived the
        /// escalation chain is the chain's final answer, so it earns a new Character.
        /// </summary>
        private async Task ApplySegmentsAsync(
            QueuedParagraph item, IReadOnlyList<AttributionSegment> segments, CancellationToken ct)
        {
            var specs = new List<SegmentSpec>(segments.Count);
            foreach (var segment in segments)
            {
                var isNarration = segment.Type == AttributionSegmentType.Narration;
                specs.Add(new SegmentSpec(
                    segment.Text,
                    isNarration ? SegmentItemType.Narration : SegmentItemType.Character,
                    isNarration ? null : await ResolveSpeakerAsync(item.Folder, segment.Speaker, ct),
                    string.IsNullOrWhiteSpace(segment.VoiceInstructions) ? null : segment.VoiceInstructions));
            }

            await commands.ExecuteAsync(
                new ApplySegmentationCommand(item.Folder, item.ParagraphId, specs), ct);
        }

        /// <summary>Unknown speaker → null (nobody to stamp); any other name resolves or is created.</summary>
        private async Task<Guid?> ResolveSpeakerAsync(ProjectFolderId folder, string speaker, CancellationToken ct) =>
            SegmentWire.IsUnknownSpeaker(speaker)
                ? null
                : await resolver.ResolveOrCreateAsync(folder, speaker.Trim(), ct);
    }
}
