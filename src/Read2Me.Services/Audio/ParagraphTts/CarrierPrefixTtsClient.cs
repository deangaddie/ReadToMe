using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.Transcription;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Decorator over <see cref="IParagraphTtsClient"/> that stabilises very short synthesis
    /// text. When enabled and the target text is at or below <c>CarrierMaxTargetChars</c>, the
    /// voice's reference transcript is prepended as carrier text (the model reproduces its own
    /// cloning reference reliably), the combined audio is synthesised in one inner call, then
    /// the carrier portion is located via word-timestamped transcription and trimmed off.
    ///
    /// Any trim failure (no transcription config, transcription error, alignment failure)
    /// logs a warning and returns the untrimmed combined audio — the pipeline's WER verify
    /// and bounded retry act as the safety net. Cancellation propagates.
    /// </summary>
    public sealed class CarrierPrefixTtsClient(
        IParagraphTtsClient inner,
        ITranscriptionClientResolver transcriptionResolver,
        TranscriptionSettingsService transcriptionSettings,
        ILogger<CarrierPrefixTtsClient> logger) : IParagraphTtsClient
    {
        public async Task<Stream> GenerateAsync(
            string text,
            string? voiceInstructions,
            Stream referenceAudioStream,
            ParagraphTtsServiceConfig config,
            string? settingsOverrideJson,
            string? referenceTranscript = null,
            CancellationToken ct = default)
        {
            // App-level fields share JSON names across all provider records, so any of them
            // deserializes the carrier settings (same precedent as SentenceChunkedTtsClient).
            var providerSettings = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(config.SettingsJson)
                ?? new VoxCpm2ParagraphTtsSettings();

            var target = text.Trim();
            if (!providerSettings.CarrierPrefixEnabled
                || target.Length == 0
                || target.Length > providerSettings.CarrierMaxTargetChars
                || string.IsNullOrWhiteSpace(referenceTranscript))
            {
                return await inner.GenerateAsync(text, voiceInstructions, referenceAudioStream, config, settingsOverrideJson, referenceTranscript, ct);
            }

            var carrier = referenceTranscript.Trim();
            if (!EndsWithTerminalPunctuation(carrier))
                carrier += ".";
            var combined = carrier + " " + target;

            logger.LogDebug(
                "Carrier prefix: target {TargetChars} chars <= {Max}, synthesising {CombinedChars} chars with carrier",
                target.Length, providerSettings.CarrierMaxTargetChars, combined.Length);

            byte[] wavBytes;
            using (var combinedWav = await inner.GenerateAsync(combined, voiceInstructions, referenceAudioStream, config, settingsOverrideJson, referenceTranscript, ct))
            {
                wavBytes = await ReadAllAsync(combinedWav, ct);
            }

            try
            {
                return await TrimCarrierAsync(wavBytes, carrier, ct)
                    ?? new MemoryStream(wavBytes, writable: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Carrier trim failed — returning untrimmed audio");
                return new MemoryStream(wavBytes, writable: false);
            }
        }

        /// <summary>Returns the trimmed WAV, or null when no confident trim is possible.</summary>
        private async Task<Stream?> TrimCarrierAsync(byte[] wavBytes, string carrier, CancellationToken ct)
        {
            var transcriptionConfig = await transcriptionSettings.GetActiveConfigAsync();
            if (transcriptionConfig is null)
            {
                logger.LogWarning("Carrier trim skipped: no active transcription config");
                return null;
            }

            var transcriptionClient = transcriptionResolver.Resolve(transcriptionConfig.Type);
            IReadOnlyList<TranscribedWord> words;
            using (var audio = new MemoryStream(wavBytes, writable: false))
            {
                words = await transcriptionClient.TranscribeWithWordTimestampsAsync(
                    transcriptionConfig, audio, "carrier-trim.wav", ct);
            }

            var boundary = CarrierAligner.FindBoundary(carrier, words);
            if (boundary is null)
            {
                logger.LogWarning(
                    "Carrier trim skipped: no confident carrier boundary in {WordCount} transcribed words",
                    words.Count);
                return null;
            }

            var cut = WavTrimmer.FindQuietestCut(
                new MemoryStream(wavBytes, writable: false),
                boundary.Value.CarrierEnd, boundary.Value.TargetStart);
            var trimmed = WavTrimmer.TrimStart(new MemoryStream(wavBytes, writable: false), cut);

            logger.LogDebug(
                "Carrier trimmed: cut at {Cut:F3}s inside gap [{CarrierEnd:F3}s, {TargetStart:F3}s]",
                cut, boundary.Value.CarrierEnd, boundary.Value.TargetStart);
            return trimmed;
        }

        private static bool EndsWithTerminalPunctuation(string s)
            => s.Length > 0 && s[^1] is '.' or '!' or '?' or '…';

        private static async Task<byte[]> ReadAllAsync(Stream s, CancellationToken ct)
        {
            if (s is MemoryStream ms)
                return ms.ToArray();
            using var copy = new MemoryStream();
            await s.CopyToAsync(copy, ct);
            return copy.ToArray();
        }
    }
}
