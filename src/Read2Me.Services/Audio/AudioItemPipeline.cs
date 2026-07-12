using System.Globalization;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Events;

namespace Read2Me.Services.Audio
{
    public sealed class AudioItemPipeline(
        IParagraphTtsClientResolver ttsResolver,
        IAudioNormalizer normalizer,
        IAudioPostProcessStepCatalog postProcessCatalog,
        IPreviewSourceCache previewSources,
        IWerComparer werComparer,
        ITranscriptionClientResolver transcriptionResolver,
        TranscriptionSettingsService transcriptionSettings,
        ISemanticVerifier semanticVerifier,
        EventBroadcaster<AudioGenEvent> broadcaster,
        IFileSystem fs,
        ILogger<AudioItemPipeline> logger) : IAudioItemPipeline
    {
        public async Task<PipelineResult> RunAsync(PipelineRequest req, CancellationToken ct)
        {
            var id = req.ParagraphItemId;
            var ttsText = ReplaceTrailingComma(req.SourceText);
            var client = ttsResolver.Resolve(req.TtsConfig.Type);

            byte[] audioBytes = [];
            NormalizeOutcome normalizeOutcome = new(false, null);
            VerifyOutcome verifyOutcome = new(false, null, null, null, false);

            for (var attempt = 1; attempt <= req.MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 1)
                    broadcaster.Publish(new ItemStarted(id, attempt, req.Speaker, req.SourceText));

                // TTS — propagate hard exceptions to caller
                using var refAudio = fs.OpenRead(req.RefAudioPath);
                var wavStream = await client.GenerateAsync(
                    ttsText, req.VoiceInstructions, refAudio, req.TtsConfig, req.TtsSettingsOverrideJson,
                    req.ReferenceTranscript, ct);
                broadcaster.Publish(new AudioGenerated(id, attempt));

                var normalizeResult = await normalizer.NormalizeAsync(wavStream, req.FfmpegPath, ct);
                var normalizeOk = normalizeResult.Status == NormalizeStatus.Normalized;
                normalizeOutcome = new NormalizeOutcome(normalizeOk, normalizeResult.Reason);
                broadcaster.Publish(new Normalized(id, attempt, normalizeOk, normalizeResult.Reason));

                audioBytes = normalizeResult.Audio is MemoryStream ms
                    ? ms.ToArray()
                    : await ReadAllBytesAsync(normalizeResult.Audio, ct);

                if (!normalizeOk)
                {
                    // Normalize fail — do not retry, return immediately
                    verifyOutcome = new VerifyOutcome(false, null, "normalize failed", null, false);
                    break;
                }

                await SavePreviewSourceAsync(req.Folder, id, audioBytes, ct);

                audioBytes = await PostProcessAsync(id, attempt, audioBytes, req.FfmpegPath, ct);

                verifyOutcome = await VerifyAsync(id, attempt, audioBytes, req.SourceText, req.WerThreshold, ct);

                if (verifyOutcome.Ok)
                    break;

                // Only real verify fail retries: transcript exists + wer over threshold + not rescued
                var isRealVerifyFail = verifyOutcome.Transcript is not null
                    && verifyOutcome.Wer.HasValue
                    && !verifyOutcome.Rescued;

                if (isRealVerifyFail && attempt < req.MaxAttempts)
                {
                    logger.LogDebug("Item {Id} attempt {A} real verify fail (wer {W}); retrying", id, attempt, verifyOutcome.Wer);
                    continue;
                }

                break;
            }

            return new PipelineResult(audioBytes, normalizeOutcome, verifyOutcome);
        }

        /// Parks the normalized-but-not-yet-stepped audio as this item's Preview Source, so the A/B
        /// preview has something honest to filter — the audio that ends up in {id}.wav has already
        /// been through the steps. Retries just overwrite, so the cache always holds the bytes behind
        /// the attempt that was kept. The preview is cosmetic and the cache is scratch, so *nothing* it
        /// can throw is worth the item's audio — every failure is swallowed, not just the I/O ones.
        private async Task SavePreviewSourceAsync(ProjectFolderId folder, Guid id, byte[] audioBytes, CancellationToken ct)
        {
            try
            {
                await previewSources.SaveAsync(folder, id, audioBytes, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Item {Id} preview source could not be cached", id);
            }
        }

        /// Runs the enabled post-process steps in stored order on normalized audio. Steps are
        /// cosmetic: a step that cannot run falls back to its input and the item still proceeds.
        private async Task<byte[]> PostProcessAsync(
            Guid id, int attempt, byte[] audioBytes, string? ffmpegPath, CancellationToken ct)
        {
            var steps = await postProcessCatalog.GetEnabledStepsAsync();

            foreach (var (step, settingsJson) in steps)
            {
                ct.ThrowIfCancellationRequested();

                var result = await step.ProcessAsync(audioBytes, ffmpegPath, settingsJson, ct);
                audioBytes = result.Audio;
                broadcaster.Publish(new PostProcessed(id, attempt, step.StepId, result.Applied, result.Reason));

                if (!result.Applied)
                    logger.LogWarning("Item {Id} attempt {A} post-process step '{Step}' skipped: {Reason}",
                        id, attempt, step.StepId, result.Reason);
            }

            return audioBytes;
        }

        private async Task<VerifyOutcome> VerifyAsync(
            Guid id, int attempt, byte[] audioBytes, string sourceText, double werThreshold, CancellationToken ct)
        {
            var config = await transcriptionSettings.GetActiveConfigAsync();
            if (config is null)
            {
                var outcome = new VerifyOutcome(false, null, "no transcription config", null, false);
                broadcaster.Publish(new Verified(id, attempt, false, null, "no transcription config", false));
                return outcome;
            }

            string transcript;
            try
            {
                var transcriptionClient = transcriptionResolver.Resolve(config.Type);
                using var audio = new MemoryStream(audioBytes);
                transcript = await transcriptionClient.TranscribeAsync(config, audio, $"{id}.wav", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Read2Me.Services.Health.AiServiceUnavailableException)
            {
                // Managed transcription service is down — let it propagate so the item requeues
                // instead of being recorded as an unverifiable failure.
                throw;
            }
            catch (Exception ex)
            {
                var reason = $"could not verify: {ex.Message}";
                broadcaster.Publish(new Verified(id, attempt, false, null, reason, false));
                return new VerifyOutcome(false, null, reason, null, false);
            }

            broadcaster.Publish(new Transcribed(id, attempt, transcript));

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
                    broadcaster.Publish(new Verified(id, attempt, true, wer, rescueReason, true));
                    return new VerifyOutcome(true, wer, rescueReason, transcript, true);
                }

                var failReason = $"WER {wer.ToString("0.00", CultureInfo.InvariantCulture)} > " +
                                 werThreshold.ToString("0.00", CultureInfo.InvariantCulture);
                broadcaster.Publish(new Verified(id, attempt, false, wer, failReason, false));
                return new VerifyOutcome(false, wer, failReason, transcript, false);
            }

            broadcaster.Publish(new Verified(id, attempt, true, wer, null, false));
            return new VerifyOutcome(true, wer, null, transcript, false);
        }

        internal static string ReplaceTrailingComma(string text)
        {
            var trimmed = text.TrimEnd();
            if (trimmed.Length == 0 || trimmed[^1] != ',')
                return text;
            return string.Concat(trimmed.AsSpan(0, trimmed.Length - 1), ";");
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
        {
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
    }
}
