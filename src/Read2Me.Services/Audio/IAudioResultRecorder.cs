using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

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

    /// <summary>
    /// The Audio Queue's write adapter (ADR 0007): it produces the take's WAV and commits the one
    /// Book mutation that records it — the audio reference and the verdict on it together, so no
    /// reader can see a row playing new audio under the previous take's review chip.
    /// <para>
    /// The WAV is <em>staged</em> beside its destination before the mutation and moved into place
    /// only after it commits. That ordering is what keeps the persisted Book from ever naming an
    /// artifact that is not there: a mutation that does not commit — the item was deleted while the
    /// take was generating, another writer held the project too long — leaves the item's existing
    /// audio exactly as it was, and the staged file is cleaned up.
    /// </para>
    /// </summary>
    public sealed class AudioResultRecorder(
        IFileSystem fs,
        BookMutations mutations,
        ILogger<AudioResultRecorder> logger) : IAudioResultRecorder
    {
        /// <summary>
        /// Records the take, or throws. The queue's processor turns a throw into a failed item, which
        /// is the honest reading of an uncommitted write: nothing was recorded, and the operator log
        /// carries why.
        /// </summary>
        /// <exception cref="InvalidOperationException">The Book mutation did not commit.</exception>
        public async Task<string> RecordAsync(
            ProjectFolderId folder,
            Guid paragraphItemId,
            PipelineResult result,
            string sourceText,
            CancellationToken ct)
        {
            var relativePath = $"audio/{paragraphItemId}.wav";
            var folderPath = fs.GetProjectFolderPath(folder.Value);
            var audioFolder = Path.Combine(folderPath, "audio");
            fs.EnsureDirectory(audioFolder);

            var destination = Path.Combine(audioFolder, $"{paragraphItemId}.wav");
            var staged = destination + ".staging";
            await fs.WriteFileAsync(staged, new MemoryStream(result.AudioBytes));

            logger.LogDebug("Item {Id} audio staged: '{Path}' ({Bytes} bytes, {Dur:0}ms)",
                paragraphItemId, staged, result.AudioBytes.Length,
                CanonicalWav.DurationMs(result.AudioBytes.Length));

            var outcome = await mutations.CommitAsync(
                new RecordParagraphItemAudioMutation(folder, paragraphItemId, relativePath, new AudioReviewVerdict(
                    result.Normalize.Ok, result.Normalize.Reason,
                    result.Verify.Ok, result.Verify.Wer, result.Verify.Reason,
                    result.Verify.Transcript, sourceText)),
                ct);

            if (outcome is not BookMutationOutcome.Committed)
            {
                Discard(staged, paragraphItemId);
                throw Uncommitted(outcome, paragraphItemId, ct);
            }

            fs.MoveFile(staged, destination);

            if (result.Normalize.Ok && result.Verify.Ok)
            {
                logger.LogDebug("Item {Id} recorded clean — review cleared (wer {Wer})",
                    paragraphItemId, result.Verify.Wer);
            }
            else
            {
                logger.LogDebug(
                    "Item {Id} recorded needs-review — normalizeOk {NormOk} ({NormReason}), " +
                    "verifyOk {VerifyOk} ({VerifyReason}), wer {Wer}",
                    paragraphItemId, result.Normalize.Ok, result.Normalize.Reason,
                    result.Verify.Ok, result.Verify.Reason, result.Verify.Wer);
            }

            return relativePath;
        }

        /// <summary>
        /// Best-effort: the take is already lost, and failing to tidy up after it must not turn one
        /// unrecorded item into a throw the queue reports as something else.
        /// </summary>
        private void Discard(string staged, Guid paragraphItemId)
        {
            try
            {
                fs.DeleteFile(staged);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the staged audio '{Path}' for item {Id}.",
                    staged, paragraphItemId);
            }
        }

        private Exception Uncommitted(BookMutationOutcome outcome, Guid paragraphItemId, CancellationToken ct)
        {
            var reason = outcome switch
            {
                BookMutationOutcome.Rejected rejected => rejected.Message,
                // Not reachable through RecordParagraphItemAudioMutation, which reports a recorded
                // take whether or not any column moved. Kept because the outcome type is closed and
                // an adapter that silently returned a path here would name audio it had discarded.
                _ => "the Book recorded no change.",
            };

            logger.LogWarning("Item {Id} audio was not recorded: {Reason}", paragraphItemId, reason);

            return outcome is BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled }
                ? new OperationCanceledException(reason, ct)
                : new InvalidOperationException($"Recording audio for item {paragraphItemId} failed: {reason}");
        }
    }
}
