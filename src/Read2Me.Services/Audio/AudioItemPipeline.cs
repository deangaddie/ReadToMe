using System.Diagnostics;
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

            var pipelineSw = Stopwatch.StartNew();
            logger.LogDebug(
                "Item {Id} pipeline start: speaker '{Speaker}', provider {Provider}, {Chars} chars, " +
                "maxAttempts {Max}, werThreshold {WerThreshold}, refAudio '{RefAudio}'",
                id, req.Speaker, req.TtsConfig.Type, req.SourceText.Length, req.MaxAttempts,
                req.WerThreshold, req.RefAudioPath);
            logger.LogTrace("Item {Id} source text: {Text}", id, req.SourceText);

            if (!string.Equals(ttsText, req.SourceText, StringComparison.Ordinal))
                logger.LogDebug("Item {Id} trailing comma replaced with semicolon for TTS", id);

            byte[] audioBytes = [];
            NormalizeOutcome normalizeOutcome = new(false, null);
            VerifyOutcome verifyOutcome = new(false, null, null, null, false);
            var attemptsUsed = 0;

            for (var attempt = 1; attempt <= req.MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                attemptsUsed = attempt;

                if (attempt > 1)
                    broadcaster.Publish(new ItemStarted(id, attempt, req.Speaker, req.SourceText));

                var attemptSw = Stopwatch.StartNew();
                logger.LogDebug("Item {Id} attempt {A}/{Max} start", id, attempt, req.MaxAttempts);

                // TTS — propagate hard exceptions to caller
                logger.LogDebug("Item {Id} attempt {A}: TTS start ({Chars} chars via {Provider})",
                    id, attempt, ttsText.Length, req.TtsConfig.Type);
                var ttsSw = Stopwatch.StartNew();
                using var refAudio = fs.OpenRead(req.RefAudioPath);
                var wavStream = await client.GenerateAsync(
                    ttsText, req.VoiceInstructions, refAudio, req.TtsConfig, req.TtsSettingsOverrideJson,
                    req.ReferenceTranscript, ct);
                ttsSw.Stop();
                // Raw TTS output is not canonical WAV yet (providers emit their own sample rate),
                // so bytes only — duration is meaningful from the normalize step onward.
                logger.LogDebug("Item {Id} attempt {A}: TTS complete — {Bytes} bytes in {Ms} ms",
                    id, attempt, StreamLength(wavStream), ttsSw.ElapsedMilliseconds);
                broadcaster.Publish(new AudioGenerated(id, attempt));

                logger.LogDebug("Item {Id} attempt {A}: normalize start (loudnorm)", id, attempt);
                var normalizeSw = Stopwatch.StartNew();
                var normalizeResult = await normalizer.NormalizeAsync(wavStream, req.FfmpegPath, ct);
                normalizeSw.Stop();
                var normalizeOk = normalizeResult.Status == NormalizeStatus.Normalized;
                normalizeOutcome = new NormalizeOutcome(normalizeOk, normalizeResult.Reason);
                broadcaster.Publish(new Normalized(id, attempt, normalizeOk, normalizeResult.Reason));

                audioBytes = normalizeResult.Audio is MemoryStream ms
                    ? ms.ToArray()
                    : await ReadAllBytesAsync(normalizeResult.Audio, ct);

                logger.LogDebug(
                    "Item {Id} attempt {A}: normalize complete — {Status} ({Bytes} bytes, {Dur:0}ms audio) " +
                    "in {Ms} ms{Reason}",
                    id, attempt, normalizeResult.Status, audioBytes.Length,
                    CanonicalWav.DurationMs(audioBytes.Length), normalizeSw.ElapsedMilliseconds,
                    normalizeResult.Reason is null ? "" : $" — {normalizeResult.Reason}");

                if (!normalizeOk)
                {
                    // Normalize fail — do not retry, return immediately
                    logger.LogDebug("Item {Id} attempt {A}: normalize failed — no retry, no verify", id, attempt);
                    verifyOutcome = new VerifyOutcome(false, null, "normalize failed", null, false);
                    break;
                }

                await SavePreviewSourceAsync(req.Folder, id, audioBytes, ct);

                audioBytes = await PostProcessAsync(id, attempt, audioBytes, req.FfmpegPath, ct);

                verifyOutcome = await VerifyAsync(id, attempt, audioBytes, req.SourceText, req.WerThreshold, ct);

                attemptSw.Stop();
                logger.LogDebug("Item {Id} attempt {A} complete in {Ms} ms — verifyOk {Ok}",
                    id, attempt, attemptSw.ElapsedMilliseconds, verifyOutcome.Ok);

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

                if (!isRealVerifyFail)
                    logger.LogDebug("Item {Id} attempt {A}: not a real verify fail ({Reason}) — no retry",
                        id, attempt, verifyOutcome.Reason);

                break;
            }

            pipelineSw.Stop();
            logger.LogDebug(
                "Item {Id} pipeline complete in {Ms} ms — {Attempts} attempt(s), normalizeOk {NormOk}, " +
                "verifyOk {VerifyOk}, wer {Wer}, {Bytes} bytes ({Dur:0}ms audio)",
                id, pipelineSw.ElapsedMilliseconds, attemptsUsed, normalizeOutcome.Ok, verifyOutcome.Ok,
                verifyOutcome.Wer, audioBytes.Length, CanonicalWav.DurationMs(audioBytes.Length));

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

        /// Runs the enabled post-process steps in the catalog's (code-defined) order on normalized
        /// audio, over the shared fold in <see cref="AudioPostProcessChain"/>. Steps are cosmetic: a
        /// step that cannot run falls back to its input and the item still proceeds. The catalog
        /// supplies *which* steps (paragraph scope); the fold only folds.
        private async Task<byte[]> PostProcessAsync(
            Guid id, int attempt, byte[] audioBytes, string? ffmpegPath, CancellationToken ct)
        {
            var steps = await postProcessCatalog.GetEnabledStepsAsync();

            logger.LogDebug("Item {Id} attempt {A}: post-process start — {Count} enabled step(s) [{Steps}]",
                id, attempt, steps.Count, string.Join(", ", steps.Select(s => s.Step.StepId)));

            var chain = steps.Select(s => new ResolvedStep(s.Step, s.SettingsJson)).ToList();
            var result = await AudioPostProcessChain.FoldAsync(audioBytes, chain, ffmpegPath, logger, ct);

            foreach (var outcome in result.Steps)
                broadcaster.Publish(new PostProcessed(id, attempt, outcome.StepId, outcome.Applied, outcome.Reason));

            logger.LogDebug("Item {Id} attempt {A}: post-process complete — {Bytes} bytes ({Dur:0}ms audio)",
                id, attempt, result.Audio.Length, CanonicalWav.DurationMs(result.Audio.Length));

            return result.Audio;
        }

        private async Task<VerifyOutcome> VerifyAsync(
            Guid id, int attempt, byte[] audioBytes, string sourceText, double werThreshold, CancellationToken ct)
        {
            var config = await transcriptionSettings.GetActiveConfigAsync();
            if (config is null)
            {
                logger.LogDebug("Item {Id} attempt {A}: verify skipped — no active transcription config", id, attempt);
                var outcome = new VerifyOutcome(false, null, "no transcription config", null, false);
                broadcaster.Publish(new Verified(id, attempt, false, null, "no transcription config", false));
                return outcome;
            }

            logger.LogDebug("Item {Id} attempt {A}: transcribe start ({Provider}, {Dur:0}ms audio)",
                id, attempt, config.Type, CanonicalWav.DurationMs(audioBytes.Length));

            string transcript;
            var transcribeSw = Stopwatch.StartNew();
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
                logger.LogWarning(ex, "Item {Id} attempt {A}: transcribe failed — {Reason}", id, attempt, reason);
                broadcaster.Publish(new Verified(id, attempt, false, null, reason, false));
                return new VerifyOutcome(false, null, reason, null, false);
            }
            transcribeSw.Stop();

            logger.LogDebug("Item {Id} attempt {A}: transcribe complete in {Ms} ms — transcript: {Transcript}",
                id, attempt, transcribeSw.ElapsedMilliseconds, transcript);
            logger.LogDebug("Item {Id} attempt {A}: source text for WER: {Source}", id, attempt, sourceText);

            broadcaster.Publish(new Transcribed(id, attempt, transcript));

            var wer = werComparer.Compute(sourceText, transcript);
            logger.LogDebug("Item {Id} attempt {A}: WER {Wer:0.000} (threshold {Threshold:0.000}) — {Verdict}",
                id, attempt, wer, werThreshold, wer > werThreshold ? "over" : "pass");

            if (wer > werThreshold)
            {
                logger.LogDebug("Item {Id} attempt {A}: semantic rescue check start", id, attempt);
                var (semanticPass, semanticScore, semanticThreshold) =
                    await semanticVerifier.PassesAsync(sourceText, transcript, ct);
                logger.LogDebug(
                    "Item {Id} attempt {A}: semantic rescue {Verdict} — score {Score}, threshold {Threshold}",
                    id, attempt, semanticPass ? "passed" : "failed", semanticScore, semanticThreshold);
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

        private static long StreamLength(Stream s) => s.CanSeek ? s.Length : 0;

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
        {
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
    }
}
