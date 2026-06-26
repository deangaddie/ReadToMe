using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;

namespace Read2Me.App.Audio
{
    public sealed class AudioQueueProcessor(
        AudioQueueService queue,
        IAudioItemResolver resolver,
        IAudioItemPipeline pipeline,
        IBookCommandHandler commands,
        IFileSystem fs,
        AudioReviewService reviews,
        AudioGenBroadcaster broadcaster,
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
                    broadcaster.Publish(new Failed(itemRef.ParagraphItemId, Attempt: 1, resolution.FailureReason!));
                    queue.MarkFailed(folder, itemRef, resolution.FailureReason!);
                    return;
                }

                var req = resolution.Request!;

                logger.LogInformation("Pipeline starting for item {ItemId} speaker {Speaker} maxAttempts {Max}",
                    itemRef.ParagraphItemId, req.Speaker, req.MaxAttempts);

                var result = await pipeline.RunAsync(req, ct);

                logger.LogInformation("Pipeline complete for item {ItemId} normalizeOk={NormalizeOk} verifyOk={VerifyOk}",
                    itemRef.ParagraphItemId, result.Normalize.Ok, result.Verify.Ok);

                var folderPath = fs.GetProjectFolderPath(folder.Value);
                var relativePath = $"audio/{itemRef.ParagraphItemId}.wav";
                var audioFolder = System.IO.Path.Combine(folderPath, "audio");
                fs.EnsureDirectory(audioFolder);
                var outPath = System.IO.Path.Combine(audioFolder, $"{itemRef.ParagraphItemId}.wav");
                await fs.WriteFileAsync(outPath, new MemoryStream(result.AudioBytes));

                await commands.ExecuteAsync(
                    new SetParagraphItemAudioCommand(folder, itemRef.ParagraphItemId, relativePath), ct);

                await commands.ExecuteAsync(
                    new SetAudioReviewCommand(
                        folder, itemRef.ParagraphItemId,
                        result.Normalize.Ok, result.Normalize.Reason,
                        result.Verify.Ok, result.Verify.Wer, result.Verify.Reason,
                        result.Verify.Transcript, req.SourceText), ct);

                if (result.Normalize.Ok && result.Verify.Ok)
                {
                    reviews.Clear(folder, itemRef.ParagraphItemId);
                }
                else
                {
                    reviews.Set(folder, itemRef.ParagraphItemId, new AudioReviewInfo(
                        Core.Models.AudioReviewState.NeedsReview,
                        result.Normalize.Ok, result.Normalize.Reason,
                        result.Verify.Ok, result.Verify.Wer, result.Verify.Reason,
                        result.Verify.Transcript, req.SourceText));
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
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed processing audio item {ItemId}", itemRef.ParagraphItemId);
                broadcaster.Publish(new Failed(itemRef.ParagraphItemId, Attempt: 1, ex.Message));
                queue.MarkFailed(folder, itemRef, ex.Message);
            }
        }
    }
}
