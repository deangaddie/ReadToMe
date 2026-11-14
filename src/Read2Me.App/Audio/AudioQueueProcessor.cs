using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Audio
{
    public sealed class AudioQueueProcessor(
        IAudioQueue queue,
        IAudioItemResolver resolver,
        IAudioItemPipeline pipeline,
        IAudioResultRecorder recorder,
        EventBroadcaster<AudioGenEvent> broadcaster,
        ILogger<AudioQueueProcessor> logger) : IAudioQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedAudioItem queued, CancellationToken hostCt)
        {
            // Cancel all stops the item in flight as well as the waiting ones.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt, queue.ItemCancellationToken);
            var ct = linked.Token;

            queue.MarkProcessing(queued);

            try
            {
                var disposition = await DecideAsync(queued, ct);
                Report(queued, disposition);
                queue.Apply(queued, disposition);
            }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Cancel all, which has already cleared the item: it applies and publishes nothing.
                // The resolver can also throw one of its own; the pipeline and the recorder throw
                // only for the token.
                logger.LogInformation("Cancelled audio item {ItemId}", queued.Item.ParagraphItemId);
            }
        }

        /// <summary>
        /// Decides the item's fate. The policy itself is not here: phase 1 is
        /// <see cref="QueueDisposition.Decide"/> — provider behaviour and retry budgets, shared with
        /// the character queue — and phase 2 is <see cref="AudioDisposition.DecideApplied"/>. What
        /// stays is the work, which needs this processor's collaborators.
        /// </summary>
        private async Task<Disposition> DecideAsync(QueuedAudioItem queued, CancellationToken ct)
        {
            var itemId = queued.Item.ParagraphItemId;

            ResolutionResult resolution;
            try
            {
                resolution = await resolver.ResolveAsync(queued, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // Resolution reads the book and the voice settings. Nothing here is an AI seam, so a
            // throw is an ordinary failure — and must still settle the item rather than leave it
            // stuck in Processing.
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed resolving audio item {ItemId}", itemId);
                return new Disposition.Failed(ex.Message);
            }

            broadcaster.Publish(new ItemStarted(itemId, Attempt: 1, resolution.Speaker, resolution.SourceText));

            if (!resolution.Succeeded)
            {
                logger.LogWarning("Resolution failed for item {ItemId}: {Reason}",
                    itemId, resolution.FailureReason);
                return new Disposition.Failed(resolution.FailureReason);
            }

            var req = resolution.Request!;

            logger.LogInformation("Pipeline starting for item {ItemId} speaker {Speaker} maxAttempts {Max}",
                itemId, req.Speaker, req.MaxAttempts);

            var result = await pipeline.RunAsync(req, ct);

            logger.LogInformation("Pipeline complete for item {ItemId} outcome={Outcome} normalizeOk={NormalizeOk} verifyOk={VerifyOk}",
                itemId, result.Outcome.GetType().Name, result.Normalize.Ok, result.Verify.Ok);

            // The pipeline is total, so an AI outage arrives as a value. Ok always carries audio worth
            // recording — this queue's own empty case is a failed resolution, which never reaches
            // here — so hasApplicableWork is constant and the shared Unfinished arm is unreachable.
            var plan = QueueDisposition.Decide(result.Outcome, hasApplicableWork: true, queued.Attempts);

            return plan switch
            {
                Plan.ApplyFirst => await RecordAndDecideAsync(queued, req, result, ct),

                Plan.Now now => now.D,

                _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unhandled Plan."),
            };
        }

        /// <summary>
        /// The <see cref="Plan.ApplyFirst"/> branch: write the audio out, then hand the recorder's own
        /// product to phase 2. The relative path exists only after the apply, which is why this queue
        /// takes the branch at all.
        /// </summary>
        private async Task<Disposition> RecordAndDecideAsync(
            QueuedAudioItem queued, PipelineRequest req, PipelineResult result, CancellationToken ct)
        {
            var itemId = queued.Item.ParagraphItemId;

            // A Restore that began while the take generated is the shared step's: it generates
            // the take again afterwards.
            return await ApplyFirstStep.RunAsync(async () =>
            {
                try
                {
                    var relativePath = await recorder.RecordAsync(queued.Folder, itemId, result, req.SourceText, ct);
                    return AudioDisposition.DecideApplied(relativePath);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                // The one catch that survives the pipeline going total, and it is not an AI seam:
                // recording writes the audio file and can fail on ordinary I/O.
                catch (Exception ex) when (ex is not ProjectRestoringException)
                {
                    logger.LogError(ex, "Failed recording audio item {ItemId}", itemId);
                    return new Disposition.Failed(ex.Message);
                }
            });
        }

        /// <summary>
        /// Narrates the decided transition, to the operator log and — for a failure — to the live
        /// generation event stream. Running it is <see cref="IAudioQueue.Apply"/>'s job; only the
        /// story of why belongs to the processor.
        /// </summary>
        private void Report(QueuedAudioItem item, Disposition disposition)
        {
            var itemId = item.Item.ParagraphItemId;
            switch (disposition)
            {
                case Disposition.Complete complete:
                    logger.LogInformation("Completed audio item {ItemId} at {Path}", itemId, complete.Product);
                    break;

                case Disposition.Unfinished unfinished:
                    logger.LogInformation("Audio item {ItemId} unfinished: {Reason}", itemId, unfinished.Reason);
                    break;

                case Disposition.Failed failed:
                    var message = failed.Reason ?? "audio generation failed";
                    logger.LogWarning("Audio item {ItemId} failed: {Reason}", itemId, message);
                    broadcaster.Publish(new Failed(itemId, Attempt: 1, message));
                    break;

                case Disposition.RetryOnce:
                    logger.LogInformation("Audio item {ItemId} service unavailable — requeuing", itemId);
                    break;

                case Disposition.RetryAfter retryAfter:
                    logger.LogInformation(
                        "Audio item {ItemId} provider busy — requeuing in {Backoff:0.#}s (attempt {Attempt})",
                        itemId, retryAfter.Delay.TotalSeconds, item.Attempts.Busies + 1);
                    break;

                case Disposition.RetryAfterRestore:
                    logger.LogInformation("Audio item {ItemId} project is restoring — requeuing after the Restore", itemId);
                    break;
            }
        }
    }
}
