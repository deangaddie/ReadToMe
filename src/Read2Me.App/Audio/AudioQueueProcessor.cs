using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Voice;

namespace Read2Me.App.Audio
{
    public sealed class AudioQueueProcessor(
        AudioQueueService queue,
        ParagraphTtsSettingsService ttsSettings,
        IParagraphTtsClientResolver ttsResolver,
        IBookCommandHandler commands,
        IFileSystem fs,
        IProjectDbContextFactory dbFactory,
        IProjectReader reader,
        IAudioNormalizer normalizer,
        IWerComparer werComparer,
        ITranscriptionClientResolver transcriptionResolver,
        TranscriptionSettingsService transcriptionSettings,
        AudioProcessingSettingsService audioProcessingSettings,
        AudioReviewService reviews,
        AudioGenBroadcaster broadcaster,
        ISemanticVerifier semanticVerifier,
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
                    broadcaster.Publish(new ItemStarted(itemRef.ParagraphItemId, Attempt: 1, null, null));
                    broadcaster.Publish(new Failed(itemRef.ParagraphItemId, Attempt: 1,
                        $"ParagraphItem {itemRef.ParagraphItemId} not found"));
                    queue.MarkFailed(folder, itemRef, $"ParagraphItem {itemRef.ParagraphItemId} not found");
                    return;
                }

                var speaker = row.ItemType == ParagraphItemType.Narration
                    ? "Narrator"
                    : row.Character?.Name;

                // Published here for hard Item-failure paths (no character, no voice, no TTS config)
                // that short-circuit before the retry loop. The loop republishes ItemStarted per attempt.
                broadcaster.Publish(new ItemStarted(itemRef.ParagraphItemId, Attempt: 1, speaker, row.Text));

                var narratorOnly = await db.Projects
                    .AsNoTracking()
                    .Select(p => p.NarratorOnlyMode)
                    .FirstOrDefaultAsync(ct);

                var characterId = narratorOnly || row.ItemType == ParagraphItemType.Narration
                    ? ProjectDbContext.NarratorId
                    : row.CharacterId;

                if (characterId is null)
                {
                    broadcaster.Publish(new Failed(itemRef.ParagraphItemId, Attempt: 1, "No character assigned to item"));
                    queue.MarkFailed(folder, itemRef, "No character assigned to item");
                    return;
                }

                var (itemPos, ruleInputs) = await reader.GetVoiceRuleInputsAsync(folder, itemRef.ParagraphItemId, characterId.Value);
                var selectedVoiceId = VoiceRuleEvaluator.Evaluate(ruleInputs, itemPos);

                var voice = selectedVoiceId.HasValue
                    ? await db.Voices
                        .AsNoTracking()
                        .Include(v => v.Character)
                        .FirstOrDefaultAsync(v => v.Id == selectedVoiceId.Value, ct)
                    : null;

                if (voice is null)
                {
                    var charName = row.ItemType == ParagraphItemType.Narration
                        ? "Narrator"
                        : (row.Character?.Name ?? characterId.ToString());
                    broadcaster.Publish(new Failed(itemRef.ParagraphItemId, Attempt: 1, $"No default voice for {charName}"));
                    queue.MarkFailed(folder, itemRef, $"No default voice for {charName}");
                    return;
                }

                var config = await ttsSettings.GetActiveConfigAsync();
                if (config is null)
                {
                    broadcaster.Publish(new Failed(itemRef.ParagraphItemId, Attempt: 1, "No active TTS configuration"));
                    queue.MarkFailed(folder, itemRef, "No active TTS configuration");
                    return;
                }

                var client = ttsResolver.Resolve(config.Type);

                var refAudioPath = Path.Combine(folderPath, voice.AudioFileName!.Replace('/', Path.DirectorySeparatorChar));

                var sourceText = row.Text ?? string.Empty;
                // A trailing comma marks a sentence that continues in the next item. Some TTS
                // services treat the dangling comma as a cue to invent an ending (e.g. turning
                // it into a greeting). Swap it for a semicolon, which reads as a neutral pause.
                var ttsText = ReplaceTrailingComma(sourceText);

                var processingSettings = await audioProcessingSettings.GetAsync();
                var ffmpegPath = processingSettings.FfmpegPath;
                var werThreshold = processingSettings.WerThreshold;
                var maxAttempts = Math.Max(1, processingSettings.AudioMaxAttempts);

                logger.LogInformation("Audio retry loop starting for item {ItemId} speaker {Speaker} maxAttempts {Max}",
                    itemRef.ParagraphItemId, speaker, maxAttempts);

                // --- Retry loop: generate → normalize → verify, up to maxAttempts times.
                //     Only the final attempt is persisted.
                byte[] audioBytes = [];
                bool normalizeOk = false;
                string? normalizeReason = null;
                bool verifyOk = false;
                double? wer = null;
                string? verifyReason = null;
                string? transcript = null;
                bool rescued = false;

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Attempt 1 ItemStarted was already published above (before the hard-fail guard paths).
                    if (attempt > 1)
                        broadcaster.Publish(new ItemStarted(itemRef.ParagraphItemId, attempt, speaker, row.Text));

                    using var refAudio = fs.OpenRead(refAudioPath);
                    var wavStream = await client.GenerateAsync(ttsText, row.VoiceInstructions, refAudio, config, voice.TtsSettingsOverrideJson, ct);
                    broadcaster.Publish(new AudioGenerated(itemRef.ParagraphItemId, attempt));

                    var normalizeResult = await normalizer.NormalizeAsync(wavStream, ffmpegPath, ct);
                    normalizeOk = normalizeResult.Status == NormalizeStatus.Normalized;
                    normalizeReason = normalizeResult.Reason;
                    broadcaster.Publish(new Normalized(itemRef.ParagraphItemId, attempt, normalizeOk, normalizeReason));

                    audioBytes = normalizeResult.Audio is MemoryStream ms2
                        ? ms2.ToArray()
                        : await ReadAllBytesAsync(normalizeResult.Audio, ct);

                    (verifyOk, wer, verifyReason, transcript, rescued) =
                        await VerifyAsync(audioBytes, $"{itemRef.ParagraphItemId}.wav", sourceText, werThreshold, ct);

                    if (transcript is not null)
                        broadcaster.Publish(new Transcribed(itemRef.ParagraphItemId, attempt, transcript));
                    broadcaster.Publish(new Verified(itemRef.ParagraphItemId, attempt, verifyOk, wer, verifyReason, rescued));

                    logger.LogDebug(
                        "Item {ItemId} attempt {Attempt}/{Max}: normalizeOk={NormalizeOk} verifyOk={VerifyOk} wer={Wer} rescued={Rescued}",
                        itemRef.ParagraphItemId, attempt, maxAttempts, normalizeOk, verifyOk, wer, rescued);

                    if (verifyOk)
                        break;

                    // Real verify failure: transcript exists, wer over threshold, not rescued — retryable.
                    var isRealVerifyFail = transcript is not null && wer.HasValue && !rescued;
                    if (isRealVerifyFail && attempt < maxAttempts)
                    {
                        logger.LogDebug(
                            "Item {ItemId} attempt {Attempt} failed verify (wer {Wer} > {Threshold}, semantic not rescued); retrying",
                            itemRef.ParagraphItemId, attempt, wer, werThreshold);
                        continue;
                    }

                    if (isRealVerifyFail)
                        logger.LogDebug("Item {ItemId} exhausted {Max} attempts", itemRef.ParagraphItemId, maxAttempts);
                    break;
                }

                logger.LogInformation("Audio complete for item {ItemId} attemptsUsed=? finalVerifyOk={VerifyOk}",
                    itemRef.ParagraphItemId, verifyOk);

                // --- Persist final attempt only.
                var relativePath = $"audio/{itemRef.ParagraphItemId}.wav";
                var audioFolder = Path.Combine(folderPath, "audio");
                fs.EnsureDirectory(audioFolder);
                var outPath = Path.Combine(audioFolder, $"{itemRef.ParagraphItemId}.wav");
                await fs.WriteFileAsync(outPath, new MemoryStream(audioBytes));

                await commands.ExecuteAsync(
                    new SetParagraphItemAudioCommand(folder, itemRef.ParagraphItemId, relativePath), ct);

                await commands.ExecuteAsync(
                    new SetAudioReviewCommand(
                        folder, itemRef.ParagraphItemId,
                        normalizeOk, normalizeReason,
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
                        normalizeOk, normalizeReason,
                        verifyOk, wer, verifyReason,
                        transcript, sourceText));
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

        private async Task<(bool VerifyOk, double? Wer, string? Reason, string? Transcript, bool Rescued)> VerifyAsync(
            byte[] audioBytes, string fileName, string sourceText, double werThreshold, CancellationToken ct)
        {
            var config = await transcriptionSettings.GetActiveConfigAsync();
            if (config is null)
                return (false, null, "no transcription config", null, false);

            string transcript;
            try
            {
                var transcriptionClient = transcriptionResolver.Resolve(config.Type);
                using var audio = new MemoryStream(audioBytes);
                transcript = await transcriptionClient.TranscribeAsync(config, audio, fileName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, null, $"could not verify: {ex.Message}", null, false);
            }

            var wer = werComparer.Compute(sourceText, transcript);
            if (wer > werThreshold)
            {
                var (semanticPass, semanticScore, semanticThreshold) =
                    await semanticVerifier.PassesAsync(sourceText, transcript, ct);
                if (semanticPass)
                {
                    var rescueReason =
                        $"WER {wer.ToString("0.00", CultureInfo.InvariantCulture)} > " +
                        $"{werThreshold.ToString("0.00", CultureInfo.InvariantCulture)}; " +
                        $"rescued by semantic {semanticScore!.Value.ToString("0.00", CultureInfo.InvariantCulture)} " +
                        $">= {semanticThreshold!.Value.ToString("0.00", CultureInfo.InvariantCulture)}";
                    return (true, wer, rescueReason, transcript, true);
                }

                var reason = $"WER {wer.ToString("0.00", CultureInfo.InvariantCulture)} > " +
                             werThreshold.ToString("0.00", CultureInfo.InvariantCulture);
                return (false, wer, reason, transcript, false);
            }

            return (true, wer, null, transcript, false);
        }

        /// <summary>
        /// If the text ends in a comma (after trailing whitespace), replaces it with a semicolon.
        /// A trailing comma signals a sentence split across items; the semicolon keeps the neutral
        /// pause without prompting the TTS service to fabricate a sentence ending.
        /// </summary>
        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
        {
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }

        internal static string ReplaceTrailingComma(string text)
        {
            var trimmed = text.TrimEnd();
            if (trimmed.Length == 0 || trimmed[^1] != ',')
                return text;

            return string.Concat(trimmed.AsSpan(0, trimmed.Length - 1), ";");
        }
    }
}
