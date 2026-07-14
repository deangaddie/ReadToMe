using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Attenuates tape/preamp hiss with a band-limited spectral profile (see
    /// <see cref="HissReduceChainBuilder"/>). Voice scope only.
    /// <para>
    /// <see cref="DenoiseStep"/> removes more hiss for less damage; this step is kept for the one
    /// promise <c>anlmdn</c> cannot make — it touches nothing below 5 kHz <i>by construction</i>. It is
    /// the surgical alternative to a broadband denoise, not an addition to it.
    /// </para>
    /// </summary>
    public class HissReduceStep(ILogger<HissReduceStep> logger) : IAudioPostProcessStep
    {
        public string StepId => AudioPostProcessStepIds.HissReduce;

        public async Task<PostProcessResult> ProcessAsync(
            byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
        {
            var settings = StepSettingsJson.Parse<HissReduceSettings>(settingsJson, StepId, logger);

            string filter;
            try
            {
                filter = HissReduceChainBuilder.Build(settings);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // Only reachable if the canonical sample rate moves out from under the band profile.
                // The step contract is never-throw, so it reports rather than kills the item.
                logger.LogWarning(ex, "hiss-reduce cannot build a chain for the current canonical sample rate");
                return new PostProcessResult(wav, Applied: false, "hiss-reduce needs 24 kHz audio");
            }

            return await FfmpegFilterRunner.RunAsync(StepId, wav, ffmpegPath, filter, logger, ct);
        }
    }
}
