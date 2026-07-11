using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioPostProcessStepCatalogTests : AppDbTestBase
    {
        private sealed class FakeStep(string stepId) : IAudioPostProcessStep
        {
            public string StepId => stepId;

            public Task<PostProcessResult> ProcessAsync(byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct) =>
                Task.FromResult(new PostProcessResult(wav, Applied: true, Reason: null));
        }

        private sealed class StubProber : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ok"));
        }

        private AudioProcessingSettingsService NewSettings() =>
            new(Factory, new StubProber(), NullLogger<AudioProcessingSettingsService>.Instance);

        private AudioPostProcessStepCatalog NewCatalog(params IAudioPostProcessStep[] steps) =>
            new(steps, NewSettings());

        [Fact]
        public async Task DefaultConfig_ConsonantSoftenDisabled_ReturnsNoSteps()
        {
            var catalog = NewCatalog(new FakeStep(AudioPostProcessStepIds.ConsonantSoften));

            var enabled = await catalog.GetEnabledStepsAsync();

            Assert.Empty(enabled);
        }

        [Fact]
        public async Task EnabledStep_ReturnedWithSettingsJson()
        {
            var settings = NewSettings();
            await settings.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create(
                    AudioPostProcessStepIds.ConsonantSoften, enabled: true,
                    new ConsonantSoftenSettings { Preset = ConsonantSoftenPresets.Light }),
            });
            var catalog = NewCatalog(new FakeStep(AudioPostProcessStepIds.ConsonantSoften));

            var enabled = await catalog.GetEnabledStepsAsync();

            var entry = Assert.Single(enabled);
            Assert.Equal(AudioPostProcessStepIds.ConsonantSoften, entry.Step.StepId);
            Assert.Contains("\"light\"", entry.SettingsJson);
        }

        [Fact]
        public async Task EnabledSteps_ReturnedInStoredOrder_UnknownIdsSkipped()
        {
            var settings = NewSettings();
            await settings.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create("step-b", enabled: true, new ConsonantSoftenSettings()),
                AudioPostProcessStepConfig.Create("no-such-step", enabled: true, new ConsonantSoftenSettings()),
                AudioPostProcessStepConfig.Create("step-a", enabled: true, new ConsonantSoftenSettings()),
                AudioPostProcessStepConfig.Create("step-c", enabled: false, new ConsonantSoftenSettings()),
            });
            var catalog = NewCatalog(new FakeStep("step-a"), new FakeStep("step-b"), new FakeStep("step-c"));

            var enabled = await catalog.GetEnabledStepsAsync();

            Assert.Equal(new[] { "step-b", "step-a" }, enabled.Select(e => e.Step.StepId));
        }
    }
}
