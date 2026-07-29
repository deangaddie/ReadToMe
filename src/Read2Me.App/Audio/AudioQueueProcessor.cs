using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Audio
{
    public sealed class AudioQueueProcessor(
        AudioQueueService queue,
        IAudioItemResolver resolver,
        IAudioItemPipeline pipeline,
        IAudioResultRecorder recorder,
        EventBroadcaster<AudioGenEvent> broadcaster,
        ILogger<AudioQueueProcessor> logger) : IAudioQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedAudioItem queued, CancellationToken ct)
        {
            var (folder, itemRef) = (queued.Folder, queued.Item);
            queue.MarkProcessing(folder, itemRef);

            try
            {
                var resolution = await resolver.ResolveAsync(queued, ct);

                broadcaster.Publish(new ItemStarted(itemRef.ParagraphItemId, Attempt: 1, resolution.Speaker, resolution.SourceText));

                if (!resolution.Succeeded)
                {
                    logger.LogWarning("Resolution failed for item {ItemId}: {Reason}",
                        itemRef.ParagraphItemId, resolution.FailureReason);
                    Fail(resolution.FailureReason!);
                    return;
                }

                var req = resolution.Request!;

                logger.LogInformation("Pipeline starting for item {ItemId} speaker {Speaker} maxAttempts {Max}",
                    itemRef.ParagraphItemId, req.Speaker, req.MaxAttempts);

                var result = await pipeline.RunAsync(req, ct);

                logger.LogInformation("Pipeline complete for item {ItemId} outcome={Outcome} normalizeOk={NormalizeOk} verifyOk={VerifyOk}",
                    itemRef.ParagraphItemId, result.Outcome.GetType().Name, result.Normalize.Ok, result.Verify.Ok);

                // The pipeline is total, so an AI outage arrives as a value. Only Ok has audio worth
                // recording; audio never emits Busy, so the default arm is Failed's.
                switch (result.Outcome)
                {
                    case WorkOutcome.Ok:
                        break;

                    // Watchdog is recovering the service. Requeue once so recovery is invisible in
                    // the results; a second outage for the same item (service down) fails it.
                    case WorkOutcome.Unavailable unavailable when queued.Attempts.Retries == 0:
                        logger.LogInformation("Audio item {ItemId} service unavailable ({Reason}) — requeuing",
                            itemRef.ParagraphItemId, unavailable.Reason);
                        queue.Requeue(queued);
                        return;

                    case WorkOutcome.Unavailable unavailable:
                        logger.LogWarning("Audio item {ItemId} service unavailable again after requeue — failing",
                            itemRef.ParagraphItemId);
                        Fail(unavailable.Reason);
                        return;

                    default:
                        Fail(result.Outcome.Reason);
                        return;
                }

                string relativePath;
                try
                {
                    relativePath = await recorder.RecordAsync(folder, itemRef.ParagraphItemId, result, req.SourceText, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                // The one catch that survives the pipeline going total, and it is not an AI seam:
                // recording writes the audio file and can fail on ordinary I/O.
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed recording audio item {ItemId}", itemRef.ParagraphItemId);
                    Fail(ex.Message);
                    return;
                }

                queue.MarkComplete(folder, itemRef, relativePath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Cancelled audio item {ItemId}", itemRef.ParagraphItemId);
            }

            void Fail(string? reason)
            {
                var message = reason ?? "audio generation failed";
                broadcaster.Publish(new Failed(itemRef.ParagraphItemId, Attempt: 1, message));
                queue.MarkFailed(folder, itemRef, message);
            }
        }
    }
}
