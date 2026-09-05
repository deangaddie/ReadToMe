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
    /// Every file operation happens <em>before</em> the mutation, so the take is complete at the path
    /// the Book is about to name by the time the commit publishes its receipt. That ordering is what
    /// lets another circuit converge on that receipt and play the item at once: a Book View never
    /// names audio that is still on its way.
    /// </para>
    /// <para>
    /// The take is staged beside its destination first, and the take it replaces is set aside rather
    /// than overwritten, so a mutation that does not commit — the item was deleted while the take was
    /// generating, another writer held the project too long — puts back exactly what was there.
    /// </para>
    /// </summary>
    public sealed class AudioResultRecorder(
        IFileSystem fs,
        BookMutations mutations,
        ILogger<AudioResultRecorder> logger) : IAudioResultRecorder
    {
        /// <summary>
        /// Records the take, or throws. The queue's processor turns a throw into a failed item, which
        /// is the honest reading of an uncommitted write: nothing was recorded, the previous take is
        /// back where it was, and the operator log carries why.
        /// </summary>
        /// <exception cref="InvalidOperationException">The Book mutation did not commit.</exception>
        /// <exception cref="OperationCanceledException">
        /// It did not commit because it was cancelled. Told apart from the rest because the queue
        /// does: a cancelled item is dropped, not reported as a failure.
        /// </exception>
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

            // The take this one replaces is kept until the mutation commits: it is the only copy of
            // the audio the Book currently names, and a rejected mutation leaves the Book naming it.
            var superseded = destination + ".superseded";
            var hadPreviousTake = fs.FileExists(destination);
            if (hadPreviousTake) fs.MoveFile(destination, superseded);
            fs.MoveFile(staged, destination);

            var outcome = await mutations.CommitAsync(
                new RecordParagraphItemAudioMutation(folder, paragraphItemId, relativePath, new AudioReviewVerdict(
                    result.Normalize.Ok, result.Normalize.Reason,
                    result.Verify.Ok, result.Verify.Wer, result.Verify.Reason,
                    result.Verify.Transcript, sourceText)),
                ct);

            if (outcome is not BookMutationOutcome.Committed)
            {
                Restore(destination, superseded, hadPreviousTake, paragraphItemId);
                var failure = Uncommitted(outcome, paragraphItemId, ct);
                logger.LogWarning("Item {Id} audio was not recorded: {Reason}", paragraphItemId, failure.Message);
                throw failure;
            }

            Discard(superseded, paragraphItemId);

            logger.LogDebug(
                "Item {Id} recorded at '{Path}' — normalizeOk {NormOk} ({NormReason}), " +
                "verifyOk {VerifyOk} ({VerifyReason}), wer {Wer}",
                paragraphItemId, destination, result.Normalize.Ok, result.Normalize.Reason,
                result.Verify.Ok, result.Verify.Reason, result.Verify.Wer);

            return relativePath;
        }

        /// <summary>
        /// Puts back what the Book still names after a mutation that did not commit: the take this
        /// one was about to replace, or nothing at all if the item had none.
        /// </summary>
        private void Restore(string destination, string superseded, bool hadPreviousTake, Guid paragraphItemId)
        {
            try
            {
                if (hadPreviousTake) fs.MoveFile(superseded, destination);
                else fs.DeleteFile(destination);
            }
            catch (Exception ex)
            {
                // Best-effort: the take is already lost, and failing to tidy up after it must not
                // turn one unrecorded item into a throw the queue reports as something else.
                logger.LogWarning(ex, "Could not restore the previous audio of item {Id} at '{Path}'.",
                    paragraphItemId, destination);
            }
        }

        /// <summary>Best-effort for the same reason: a leftover file is not worth failing an item over.</summary>
        private void Discard(string path, Guid paragraphItemId)
        {
            try
            {
                if (fs.FileExists(path)) fs.DeleteFile(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the superseded audio '{Path}' of item {Id}.",
                    path, paragraphItemId);
            }
        }

        /// <summary>
        /// The write outcome as the exception the queue reads. Cancellation keeps its own type
        /// because the processor drops a cancelled item rather than reporting it as a failure.
        /// </summary>
        private static Exception Uncommitted(
            BookMutationOutcome outcome, Guid paragraphItemId, CancellationToken ct)
        {
            var reason = outcome switch
            {
                BookMutationOutcome.Rejected rejected => rejected.Message,
                // Not reachable through RecordParagraphItemAudioMutation, which reports a recorded
                // take whether or not any column moved. Kept because the outcome type is closed and
                // an adapter that silently returned a path here would name audio it had discarded.
                _ => "the Book recorded no change.",
            };

            return outcome is BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled }
                ? new OperationCanceledException(reason, ct)
                : new InvalidOperationException($"Recording audio for item {paragraphItemId} failed: {reason}");
        }
    }
}
