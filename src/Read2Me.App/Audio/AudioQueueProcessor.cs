using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.Transcription;

namespace Read2Me.App.Audio
{
    public sealed class AudioQueueProcessor(
        AudioQueueService queue,
        ParagraphTtsSettingsService ttsSettings,
        IParagraphTtsClientResolver ttsResolver,
        IBookCommandHandler commands,
        IFileSystem fs,
        IProjectDbContextFactory dbFactory,
        IAudioNormalizer normalizer,
        IWerComparer werComparer,
        ITranscriptionClientResolver transcriptionResolver,
        TranscriptionSettingsService transcriptionSettings,
        AudioProcessingSettingsService audioProcessingSettings,
        AudioReviewService reviews,
        ILogger<AudioQueueProcessor> logger) : IAudioQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedAudioItem queued, CancellationToken ct)
        {
            var (folder, itemRef) = (queued.Folder, queued.Item);
            queue.MarkProcessing(folder, itemRef);

            try
            {
                var folderPath = fs.GetProjectFolderPath(folder.Value);
                await using var db = await dbFactory.CreateAsync(folderPath);

                var row = await db.ParagraphItems
                    .AsNoTracking()
                    .Include(pi => pi.Character)
                    .FirstOrDefaultAsync(pi => pi.Id == itemRef.ParagraphItemId, ct);

                if (row is null)
                {
                    queue.MarkFailed(folder, itemRef, $"ParagraphItem {itemRef.ParagraphItemId} not found");
                    return;
                }

                var characterId = row.ItemType == ParagraphItemType.Narration
                    ? ProjectDbContext.NarratorId
                    : row.CharacterId;

                if (characterId is null)
                {
                    queue.MarkFailed(folder, itemRef, "No character assigned to item");
                    return;
                }

                var voice = await db.Voices
                    .AsNoTracking()
                    .Include(v => v.Character)
                    .FirstOrDefaultAsync(v => v.CharacterId == characterId && v.IsDefault, ct);

                if (voice is null)
                {
                    var charName = row.ItemType == ParagraphItemType.Narration
                        ? "Narrator"
                        : (row.Character?.Name ?? characterId.ToString());
                    queue.MarkFailed(folder, itemRef, $"No default voice for {charName}");
                    return;
                }

                var config = await ttsSettings.GetActiveConfigAsync();
                if (config is null)
                {
                    queue.MarkFailed(folder, itemRef, "No active TTS configuration");
                    return;
                }

                var client = ttsResolver.Resolve(config.Type);

                var refAudioPath = Path.Combine(folderPath, voice.AudioFileName!.Replace('/', Path.DirectorySeparatorChar));
                using var refAudio = fs.OpenRead(refAudioPath);

                var wavStream = await client.GenerateAsync(row.Text ?? string.Empty, row.VoiceInstructions, refAudio, config, ct);

                var sourceText = row.Text ?? string.Empty;

                // --- Post-processing pipeline (inline, blocking). Two independent stages:
                //     normalize and verify. Audio is always stored; no stage failure routes to MarkFailed.
                var (ffmpegPath, werThreshold) = await audioProcessingSettings.GetAsync();

                // Stage 1: normalize loudness. On skip the original audio is returned intact.
                var normalizeResult = await normalizer.NormalizeAsync(wavStream, ffmpegPath, ct);
                var normalizeOk = normalizeResult.Status == NormalizeStatus.Normalized;

                // Always store the single {id}.wav (normalized when ffmpeg worked, else original).
                var relativePath = $"audio/{itemRef.ParagraphItemId}.wav";
                var audioFolder = Path.Combine(folderPath, "audio");
                fs.EnsureDirectory(audioFolder);
                var outPath = Path.Combine(audioFolder, $"{itemRef.ParagraphItemId}.wav");
                await fs.WriteFileAsync(outPath, normalizeResult.Audio);

                await commands.ExecuteAsync(
                    new SetParagraphItemAudioCommand(folder, itemRef.ParagraphItemId, relativePath), ct);

                // Stage 2: verify the stored audio against the source text (runs regardless of normalize outcome).
                var (verifyOk, wer, verifyReason, transcript) =
                    await VerifyAsync(folderPath, relativePath, sourceText, werThreshold, ct);

                // Single review signal: both ok ⇒ row deleted, else upsert. Mirror in-memory for live UI.
                await commands.ExecuteAsync(
                    new SetAudioReviewCommand(
                        folder, itemRef.ParagraphItemId,
                        normalizeOk, normalizeResult.Reason,
                        verifyOk, wer, verifyReason,
                        transcript, sourceText), ct);

                if (normalizeOk && verifyOk)
                {
                    reviews.Clear(folder, itemRef.ParagraphItemId);
                }
                else
                {
                    reviews.Set(folder, itemRef.ParagraphItemId, new AudioReviewInfo(
                        Core.Models.AudioReviewState.NeedsReview,
                        normalizeOk, normalizeResult.Reason,
                        verifyOk, wer, verifyReason,
                        transcript, sourceText));
                }

                queue.MarkComplete(folder, itemRef, relativePath);

                logger.LogInformation("Audio generated for item {ItemId}", itemRef.ParagraphItemId);
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
                queue.MarkFailed(folder, itemRef, ex.Message);
            }
        }

        /// <summary>
        /// Transcribes the stored audio via the active transcription config and compares it to the
        /// source text. Each failure mode yields a distinct reason; on any non-success WER is null
        /// unless an over-threshold comparison actually produced one.
        /// </summary>
        private async Task<(bool VerifyOk, double? Wer, string? Reason, string? Transcript)> VerifyAsync(
            string folderPath, string relativePath, string sourceText, double werThreshold, CancellationToken ct)
        {
            var config = await transcriptionSettings.GetActiveConfigAsync();
            if (config is null)
                return (false, null, "no transcription config", null);

            string transcript;
            try
            {
                var transcriptionClient = transcriptionResolver.Resolve(config.Type);
                var audioPath = Path.Combine(folderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                await using var audio = fs.OpenRead(audioPath);
                transcript = await transcriptionClient.TranscribeAsync(config, audio, Path.GetFileName(audioPath), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, null, $"could not verify: {ex.Message}", null);
            }

            var wer = werComparer.Compute(sourceText, transcript);
            if (wer > werThreshold)
            {
                var reason = $"WER {wer.ToString("0.00", CultureInfo.InvariantCulture)} > " +
                             werThreshold.ToString("0.00", CultureInfo.InvariantCulture);
                return (false, wer, reason, transcript);
            }

            return (true, wer, null, transcript);
        }
    }
}
