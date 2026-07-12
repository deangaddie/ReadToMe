using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Tames harsh sibilants/plosives in TTS output with an ffmpeg consonant-soften filter chain
    /// (see <see cref="ConsonantSoftenChainBuilder"/>). Honours the never-throw / never-lose-audio
    /// contract: any ffmpeg failure (missing exe, unsupported filter, non-zero exit, timeout)
    /// returns the input unchanged with <see cref="PostProcessResult.Applied"/> false and a reason.
    /// </summary>
    public class ConsonantSoftenStep(ILogger<ConsonantSoftenStep> logger) : IAudioPostProcessStep
    {
        public string StepId => AudioPostProcessStepIds.ConsonantSoften;

        public async Task<PostProcessResult> ProcessAsync(
            byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
        {
            var settings = ParseSettings(settingsJson);
            var filter = ConsonantSoftenChainBuilder.Build(settings);

            return await FfmpegFilterRunner.RunAsync(StepId, wav, ffmpegPath, filter, logger, ct);
        }

        private ConsonantSoftenSettings? ParseSettings(string? settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<ConsonantSoftenSettings>(settingsJson, AudioPostProcessJson.Options);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "consonant-soften settings JSON malformed; using defaults");
                return null;
            }
        }
    }
}
