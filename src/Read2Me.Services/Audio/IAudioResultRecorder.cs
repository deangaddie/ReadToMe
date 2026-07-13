using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;

namespace Read2Me.Services.Audio
{
    public interface IAudioResultRecorder
    {
        Task<string> RecordAsync(
            ProjectFolderId folder,
            Guid paragraphItemId,
            PipelineResult result,
            string sourceText,
            CancellationToken ct);
    }

    public sealed class AudioResultRecorder(
        IFileSystem fs,
        IBookCommandHandler commands,
        AudioReviewService reviews,
        ILogger<AudioResultRecorder> logger) : IAudioResultRecorder
    {
        public async Task<string> RecordAsync(
            ProjectFolderId folder,
            Guid paragraphItemId,
            PipelineResult result,
            string sourceText,
            CancellationToken ct)
        {
            var relativePath = $"audio/{paragraphItemId}.wav";
            var folderPath = fs.GetProjectFolderPath(folder.Value);
            var audioFolder = System.IO.Path.Combine(folderPath, "audio");
            fs.EnsureDirectory(audioFolder);
            var outPath = System.IO.Path.Combine(audioFolder, $"{paragraphItemId}.wav");
            await fs.WriteFileAsync(outPath, new MemoryStream(result.AudioBytes));

            logger.LogDebug("Item {Id} audio written: '{Path}' ({Bytes} bytes, {Dur:0}ms)",
                paragraphItemId, outPath, result.AudioBytes.Length,
                CanonicalWav.DurationMs(result.AudioBytes.Length));

            await commands.ExecuteAsync(
                new SetParagraphItemAudioCommand(folder, paragraphItemId, relativePath), ct);

            await commands.ExecuteAsync(
                new SetAudioReviewCommand(
                    folder, paragraphItemId,
                    result.Normalize.Ok, result.Normalize.Reason,
                    result.Verify.Ok, result.Verify.Wer, result.Verify.Reason,
                    result.Verify.Transcript, sourceText), ct);

            if (result.Normalize.Ok && result.Verify.Ok)
            {
                logger.LogDebug("Item {Id} recorded clean — review cleared (wer {Wer})",
                    paragraphItemId, result.Verify.Wer);
                reviews.Clear(folder, paragraphItemId);
            }
            else
            {
                logger.LogDebug(
                    "Item {Id} recorded needs-review — normalizeOk {NormOk} ({NormReason}), " +
                    "verifyOk {VerifyOk} ({VerifyReason}), wer {Wer}",
                    paragraphItemId, result.Normalize.Ok, result.Normalize.Reason,
                    result.Verify.Ok, result.Verify.Reason, result.Verify.Wer);
                reviews.Set(folder, paragraphItemId, new AudioReviewInfo(
                    Core.Models.AudioReviewState.NeedsReview,
                    result.Normalize.Ok, result.Normalize.Reason,
                    result.Verify.Ok, result.Verify.Wer, result.Verify.Reason,
                    result.Verify.Transcript, sourceText));
            }

            return relativePath;
        }
    }
}
