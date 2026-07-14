using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Removes broadband room noise and hum from mic-recorded reference audio (see
    /// <see cref="DenoiseChainBuilder"/>). Voice scope only. Honours the never-throw /
    /// never-lose-audio contract.
    /// </summary>
    public class DenoiseStep(ILogger<DenoiseStep> logger) : IAudioPostProcessStep
    {
        public string StepId => AudioPostProcessStepIds.Denoise;

        public async Task<PostProcessResult> ProcessAsync(
            byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
        {
            var settings = StepSettingsJson.Parse<DenoiseSettings>(settingsJson, StepId, logger);
            var filter = DenoiseChainBuilder.Build(settings);

            return await FfmpegFilterRunner.RunAsync(StepId, wav, ffmpegPath, filter, logger, ct);
        }
    }
}
