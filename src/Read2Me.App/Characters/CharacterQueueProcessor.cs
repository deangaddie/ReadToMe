using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Characters
{
    internal sealed class CharacterQueueProcessor(
        ICharacterQueue queue,
        AttributionEscalationChain chain,
        CharacterResolver resolver,
        IUnattributedItemCounter reader,
        IProjectCatalogReader catalog,
        IBookCommandHandler commands,
        ILogger<CharacterQueueProcessor> logger) : ICharacterQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedParagraph item, CancellationToken hostCt)
        {
            // One narrator read per drained batch, per folder — never per segment. A drain spans
            // folders, so the cache is keyed by folder; it lives no longer than this batch, so a
            // link changed mid-book is picked up by the next one.
            var narrators = new NarratorCache(catalog);

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
                    await ApplyOutcomeAsync(streamItem, outcome, elapsed, narrators, ct);
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
                    queue.Apply(p, new Disposition.Failed(ex.Message));
            }
        }

        /// <summary>
        /// Decides the paragraph's fate and executes it. The policy itself is not here: phase 1 is
        /// <see cref="QueueDisposition.Decide"/> — provider behaviour and retry budgets, shared with
        /// the audio queue — and phase 2 is <see cref="CharacterDisposition.DecideApplied"/>. What
        /// stays is the apply and the probe, which need this processor's collaborators.
        /// </summary>
        private async Task ApplyOutcomeAsync(
            QueuedParagraph item, AttributionOutcome outcome, double elapsedSeconds,
            NarratorCache narrators, CancellationToken ct)
        {
            // The queue decides from provider behaviour, not from the answer's quality: an Ok answer
            // that left a speaker unidentified is still an answer, and whether the paragraph is
            // finished is settled after the apply, from the items. See WorkOutcome.
            var plan = QueueDisposition.Decide(outcome.Work, outcome.Segments is not null, item.Attempts);

            var disposition = plan switch
            {
                Plan.ApplyFirst => await ApplyAndDecideAsync(item, outcome, elapsedSeconds, narrators, ct),

                // Phase 1's one settling arm is the empty paragraph.
                Plan.Now { D: Disposition.Unfinished unfinished } => EmptyParagraph(item, unfinished, elapsedSeconds),

                Plan.Now now => now.D,

                _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unhandled Plan."),
            };

            Log(item, disposition);
            queue.Apply(item, disposition);
        }

        /// <summary>
        /// The <see cref="Plan.ApplyFirst"/> branch: apply the answer, probe what it left behind, and
        /// hand the residue to phase 2. The probe is only meaningful <em>after</em> a successful
        /// apply — which is why the decision that gates the apply runs first.
        /// </summary>
        private async Task<Disposition> ApplyAndDecideAsync(
            QueuedParagraph item, AttributionOutcome outcome, double elapsedSeconds,
            NarratorCache narrators, CancellationToken ct)
        {
            await ApplySegmentsAsync(item, outcome.Segments!, narrators, ct);

            var unattributed = await reader.CountUnattributedCharacterItemsAsync(item.Folder, item.ParagraphId);
            if (unattributed > 0)
                logger.LogInformation("Paragraph {ParagraphId} has {Count} unattributed item(s) after apply",
                    item.ParagraphId, unattributed);

            return CharacterDisposition.DecideApplied(unattributed, elapsedSeconds, outcome.Work.Reason);
        }

        /// <summary>
        /// Narrates the decided transition. Running it is <see cref="ICharacterQueue.Apply"/>'s job;
        /// only the operator-facing story of why belongs to the processor.
        /// </summary>
        private void Log(QueuedParagraph item, Disposition disposition)
        {
            switch (disposition)
            {
                case Disposition.Complete complete:
                    logger.LogInformation("Completed paragraph {ParagraphId} in {Elapsed:F1}s",
                        item.ParagraphId, complete.Elapsed ?? 0);
                    break;

                case Disposition.Failed failed:
                    logger.LogWarning("Paragraph {ParagraphId} failed: {Reason}",
                        item.ParagraphId, failed.Reason);
                    break;

                case Disposition.RetryOnce:
                    logger.LogInformation("Paragraph {ParagraphId} service unavailable — requeuing",
                        item.ParagraphId);
                    break;

                case Disposition.RetryAfter retryAfter:
                    logger.LogInformation(
                        "Paragraph {ParagraphId} model still loading — requeuing in {Backoff:0.#}s (attempt {Attempt})",
                        item.ParagraphId, retryAfter.Delay.TotalSeconds, item.Attempts.Busies + 1);
                    break;
            }
        }

        /// <summary>
        /// The one settling disposition phase 1 can reach: an answer with no segments to apply, so
        /// nothing was attributed. Phase 1 cannot know this queue's elapsed figure — one stopwatch
        /// spans a whole drained batch here, rather than the store measuring from
        /// <c>MarkProcessing</c> — so the queue stamps it on the way past.
        /// </summary>
        private Disposition EmptyParagraph(
            QueuedParagraph item, Disposition.Unfinished unfinished, double elapsedSeconds)
        {
            logger.LogInformation("Paragraph {ParagraphId} speaker unknown", item.ParagraphId);
            return unfinished with { Elapsed = elapsedSeconds };
        }

        /// <summary>
        /// Resolves each segment's speaker to a character id, then applies the whole list in one
        /// command. Resolution happens here, not in the handler: an unlisted name that survived the
        /// escalation chain is the chain's final answer, so it earns a new Character.
        /// </summary>
        private async Task ApplySegmentsAsync(
            QueuedParagraph item, IReadOnlyList<AttributionSegment> segments,
            NarratorCache narrators, CancellationToken ct)
        {
            var narrator = await narrators.GetAsync(item.Folder, ct);
            var specs = new List<SegmentSpec>(segments.Count);
            foreach (var segment in segments)
            {
                var isNarration = segment.Type == AttributionSegmentType.Narration;
                specs.Add(new SegmentSpec(
                    segment.Text,
                    isNarration ? SegmentItemType.Narration : SegmentItemType.Character,
                    isNarration ? null : await ResolveSpeakerAsync(item.Folder, segment.Speaker, narrator, ct),
                    string.IsNullOrWhiteSpace(segment.VoiceInstructions) ? null : segment.VoiceInstructions));
            }

            await commands.ExecuteAsync(
                new ApplySegmentationCommand(item.Folder, item.ParagraphId, specs), ct);
        }

        /// <summary>
        /// The one name→id chokepoint in the segment path. Unknown speaker → null (nobody to stamp).
        /// The narrator token on a dialog segment is a wire alias of the linked character, so it
        /// stamps that character; unlinked it stamps <b>nobody</b>, rather than crediting a spoken
        /// line to the seed Narrator row — which by definition is not in the scene, and whose stamp
        /// would make the item look attributed and hide it from the re-queue filter forever. Any
        /// other name resolves or is created.
        /// <para>
        /// Unlinked this is not a behaviour change for ordinary names: <c>Unlinked.CharacterId</c>
        /// <em>is</em> the seed id that <c>ResolveOrCreateAsync("narrator")</c> already lands on by
        /// name — only the narrator token's own answer moves.
        /// </para>
        /// </summary>
        private async Task<Guid?> ResolveSpeakerAsync(
            ProjectFolderId folder, string speaker, NarratorIdentity narrator, CancellationToken ct)
        {
            if (AttributionWire.IsUnknownSpeaker(speaker))
                return null;

            if (AttributionWire.IsNarrator(speaker))
                return narrator.IsLinked ? narrator.CharacterId : null;

            return await resolver.ResolveOrCreateAsync(folder, speaker.Trim(), ct);
        }

        /// <summary>
        /// The drained batch's narrator links, one read per folder. A drain spans folders, so a single
        /// value would be wrong; a cache per batch keeps the read off the per-segment path without
        /// outliving the work it was loaded for.
        /// </summary>
        private sealed class NarratorCache(IProjectCatalogReader catalog)
        {
            private readonly Dictionary<ProjectFolderId, NarratorIdentity> _byFolder = [];

            public async Task<NarratorIdentity> GetAsync(ProjectFolderId folder, CancellationToken ct)
            {
                if (_byFolder.TryGetValue(folder, out var known))
                    return known;

                var narrator = await catalog.GetNarratorAsync(folder, ct);
                _byFolder[folder] = narrator;
                return narrator;
            }
        }
    }
}
