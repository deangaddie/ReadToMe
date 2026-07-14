using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Cuts the subsonic thump a plosive puts into a microphone (see
    /// <see cref="DePlosiveChainBuilder"/>). Voice scope only: synthetic paragraph audio has no
    /// capture artefacts to fix. Honours the never-throw / never-lose-audio contract — any ffmpeg
    /// failure returns the input unchanged with <see cref="PostProcessResult.Applied"/> false.
    /// </summary>
    public class DePlosiveStep(ILogger<DePlosiveStep> logger) : IAudioPostProcessStep
    {
        public string StepId => AudioPostProcessStepIds.DePlosive;

        public async Task<PostProcessResult> ProcessAsync(
            byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
        {
            var settings = StepSettingsJson.Parse<DePlosiveSettings>(settingsJson, StepId, logger);
            var filter = DePlosiveChainBuilder.Build(settings);

            return await FfmpegFilterRunner.RunAsync(StepId, wav, ffmpegPath, filter, logger, ct);
        }
    }
}
