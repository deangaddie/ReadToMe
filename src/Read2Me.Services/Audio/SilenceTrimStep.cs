using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Strips leading/trailing dead air from TTS output (see <see cref="SilenceTrimChainBuilder"/>),
    /// the first step in the post-process chain. Trimming also makes assembly pacing deterministic:
    /// the gap between items becomes the configured Pause item alone, not "pause + whatever silence
    /// the TTS left". Honours the never-throw / never-lose-audio contract — any ffmpeg failure
    /// returns the input unchanged with <see cref="PostProcessResult.Applied"/> false and a reason.
    /// </summary>
    public class SilenceTrimStep(ILogger<SilenceTrimStep> logger) : IAudioPostProcessStep
    {
        /// <summary>
        /// Output shorter than this is treated as a failed trim. An absolute floor, not a
        /// percentage: a legitimate trim can remove 80%+ of a short item (a one-word "Yes."
        /// after two seconds of dead air), so a percentage rule would falsely skip exactly the
        /// case this step exists for.
        /// </summary>
        public const double MinOutputMs = 200;

        public string StepId => AudioPostProcessStepIds.SilenceTrim;

        public async Task<PostProcessResult> ProcessAsync(
            byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
        {
            var settings = ParseSettings(settingsJson);
            var filter = SilenceTrimChainBuilder.Build(settings);

            var result = await FfmpegFilterRunner.RunAsync(StepId, wav, ffmpegPath, filter, logger, ct);
            if (!result.Applied) return result;

            var trimmedMs = CanonicalWav.DurationMs(result.Audio.Length);
            if (trimmedMs < MinOutputMs)
            {
                // The TTS produced junk, or the threshold is set absurdly high. Either way the
                // reason belongs in the queue stream.
                logger.LogWarning(
                    "silence-trim would leave only {Ms:0}ms of audio; keeping the untrimmed clip", trimmedMs);
                return new PostProcessResult(wav, Applied: false, "trim would remove nearly all audio");
            }

            logger.LogInformation(
                "silence-trim removed {RemovedMs:0}ms", CanonicalWav.RemovedMs(wav.Length, result.Audio.Length));
            return result;
        }

        private SilenceTrimSettings? ParseSettings(string? settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<SilenceTrimSettings>(settingsJson, AudioPostProcessJson.Options);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "silence-trim settings JSON malformed; using defaults");
                return null;
            }
        }
    }
}
