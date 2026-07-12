using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class AudioProcessingSettingsServiceTests : AppDbTestBase
    {
        private sealed class StubProber : IFfmpegProber
        {
            public string? LastPath;
            public FfmpegProbeResult Result = new(true, "ffmpeg version test");

            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default)
            {
                LastPath = ffmpegPath;
                return Task.FromResult(Result);
            }
        }

        private AudioProcessingSettingsService NewService(IFfmpegProber? prober = null) =>
            new(Factory, prober ?? new StubProber(), NullLogger<AudioProcessingSettingsService>.Instance);

        [Fact]
        public async Task Get_MissingRow_ReturnsDefaults()
        {
            var svc = NewService();

            var settings = await svc.GetAsync();

            Assert.Null(settings.FfmpegPath);
            Assert.Equal(0.15, settings.WerThreshold);
        }

        [Fact]
        public async Task Get_MissingRow_ReturnsChunkPauseDefaults()
        {
            var svc = NewService();

            var settings = await svc.GetAsync();

            Assert.False(settings.SentenceSplitEnabled);
            Assert.Equal(300, settings.ChunkPauseMs);
        }

        [Fact]
        public async Task SetChunkPause_RoundTrips()
        {
            var svc = NewService();

            await svc.SetChunkPauseAsync(pauseMs: 750);

            var settings = await NewService().GetAsync();
            Assert.Equal(750, settings.ChunkPauseMs);
        }

        [Fact]
        public async Task SetFfmpegPath_Persists()
        {
            var svc = NewService();

            await svc.SetFfmpegPathAsync(@"C:\tools\ffmpeg.exe");

            var settings = await NewService().GetAsync();
            Assert.Equal(@"C:\tools\ffmpeg.exe", settings.FfmpegPath);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task SetFfmpegPath_Blank_StoredAsNull(string? blank)
        {
            var svc = NewService();
            await svc.SetFfmpegPathAsync(@"C:\tools\ffmpeg.exe");

            await svc.SetFfmpegPathAsync(blank);

            var settings = await NewService().GetAsync();
            Assert.Null(settings.FfmpegPath);
        }

        [Fact]
        public async Task SetWerThreshold_Persists()
        {
            var svc = NewService();

            await svc.SetWerThresholdAsync(0.42);

            var settings = await NewService().GetAsync();
            Assert.Equal(0.42, settings.WerThreshold);
        }

        [Fact]
        public async Task Setters_RaiseOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetFfmpegPathAsync("x");   // +1
            await svc.SetWerThresholdAsync(0.2); // +1

            Assert.Equal(2, count);
        }

        [Fact]
        public async Task TestFfmpeg_UsesConfiguredPath_AndReturnsProberResult()
        {
            var prober = new StubProber { Result = new(false, "not found") };
            var svc = NewService(prober);
            await svc.SetFfmpegPathAsync(@"C:\tools\ffmpeg.exe");

            var result = await svc.TestFfmpegAsync();

            Assert.Equal(@"C:\tools\ffmpeg.exe", prober.LastPath);
            Assert.False(result.Success);
            Assert.Equal("not found", result.Message);
        }

        [Fact]
        public async Task Get_MissingRow_ReturnsPauseDurationDefaults()
        {
            var svc = NewService();

            var settings = await svc.GetAsync();

            Assert.Equal(4000, settings.VolumePauseMs);
            Assert.Equal(3000, settings.PartPauseMs);
            Assert.Equal(2500, settings.ChapterPauseMs);
            Assert.Equal(800, settings.ParagraphPauseMs);
            Assert.Equal(500, settings.PauseMs);
        }

        [Fact]
        public async Task SetPauseDurations_RoundTrips()
        {
            var svc = NewService();

            await svc.SetPauseDurationsAsync(
                volumeMs: 5000, partMs: 4000, chapterMs: 3000, paragraphMs: 1000, pauseMs: 750);

            var settings = await NewService().GetAsync();
            Assert.Equal(5000, settings.VolumePauseMs);
            Assert.Equal(4000, settings.PartPauseMs);
            Assert.Equal(3000, settings.ChapterPauseMs);
            Assert.Equal(1000, settings.ParagraphPauseMs);
            Assert.Equal(750, settings.PauseMs);
        }

        [Fact]
        public async Task SetPauseDurations_RaisesOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetPauseDurationsAsync(5000, 4000, 3000, 1000, 750);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task Get_MissingRow_ReturnsDefaultAudioMaxAttempts()
        {
            var svc = NewService();

            var settings = await svc.GetAsync();

            Assert.Equal(1, settings.AudioMaxAttempts);
        }

        [Fact]
        public async Task SetAudioMaxAttempts_RoundTrips()
        {
            var svc = NewService();

            await svc.SetAudioMaxAttemptsAsync(3);

            var settings = await NewService().GetAsync();
            Assert.Equal(3, settings.AudioMaxAttempts);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public async Task SetAudioMaxAttempts_BelowOne_ClampsToOne(int value)
        {
            var svc = NewService();

            await svc.SetAudioMaxAttemptsAsync(value);

            var settings = await NewService().GetAsync();
            Assert.Equal(1, settings.AudioMaxAttempts);
        }

        [Fact]
        public async Task SetAudioMaxAttempts_RaisesOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetAudioMaxAttemptsAsync(2);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task GetPostProcessSteps_MissingRow_ReturnsCodeDefaults_InPipelineOrder()
        {
            var svc = NewService();

            var steps = await svc.GetPostProcessStepsAsync();

            Assert.Equal(
                new[] { AudioPostProcessStepIds.SilenceTrim, AudioPostProcessStepIds.ConsonantSoften },
                steps.Select(s => s.StepId));

            var trim = steps[0];
            Assert.True(trim.Enabled);
            var trimSettings = trim.GetSettings<SilenceTrimSettings>();
            Assert.NotNull(trimSettings);
            Assert.Equal(-50, trimSettings.ThresholdDb);
            Assert.Equal(50, trimSettings.PadMs);

            var soften = steps[1];
            Assert.False(soften.Enabled);
            var softenSettings = soften.GetSettings<ConsonantSoftenSettings>();
            Assert.NotNull(softenSettings);
            Assert.Equal(ConsonantSoftenEngines.AdynEq, softenSettings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Strong, softenSettings.Preset);
        }

        [Fact]
        public async Task SetPostProcessSteps_PresetRef_RoundTrips()
        {
            var svc = NewService();
            var config = AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, enabled: true,
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.Deesser, Preset = ConsonantSoftenPresets.Light });

            await svc.SetPostProcessStepsAsync(new[] { config });

            var step = (await NewService().GetPostProcessStepsAsync())
                .Single(s => s.StepId == AudioPostProcessStepIds.ConsonantSoften);
            Assert.True(step.Enabled);
            var settings = step.GetSettings<ConsonantSoftenSettings>();
            Assert.NotNull(settings);
            Assert.Equal(ConsonantSoftenEngines.Deesser, settings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Light, settings.Preset);
            Assert.Null(settings.AdynEq);
            Assert.Null(settings.Deesser);
        }

        [Fact]
        public async Task SetPostProcessSteps_SilenceTrimRawParams_RoundTrip()
        {
            var svc = NewService();

            await svc.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create(
                    AudioPostProcessStepIds.SilenceTrim, enabled: false,
                    new SilenceTrimSettings(ThresholdDb: -35, PadMs: 0)),
            });

            var step = (await NewService().GetPostProcessStepsAsync())
                .Single(s => s.StepId == AudioPostProcessStepIds.SilenceTrim);
            Assert.False(step.Enabled);
            var settings = step.GetSettings<SilenceTrimSettings>();
            Assert.NotNull(settings);
            Assert.Equal(-35, settings.ThresholdDb);
            Assert.Equal(0, settings.PadMs);
        }

        [Fact]
        public async Task UpsertPostProcessStep_LeavesTheOtherStepsAlone()
        {
            var svc = NewService();
            await svc.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create(
                    AudioPostProcessStepIds.SilenceTrim, enabled: false, new SilenceTrimSettings(PadMs: 120)),
                AudioPostProcessStepConfig.Create(
                    AudioPostProcessStepIds.ConsonantSoften, enabled: false, new ConsonantSoftenSettings()),
            });

            await svc.UpsertPostProcessStepAsync(AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, enabled: true,
                new ConsonantSoftenSettings { Preset = ConsonantSoftenPresets.Light }));

            var steps = await NewService().GetPostProcessStepsAsync();
            Assert.Equal(2, steps.Count);
            Assert.False(steps[0].Enabled);
            Assert.Equal(120, steps[0].GetSettings<SilenceTrimSettings>()!.PadMs);
            Assert.True(steps[1].Enabled);
            Assert.Equal(ConsonantSoftenPresets.Light, steps[1].GetSettings<ConsonantSoftenSettings>()!.Preset);
        }

        [Fact]
        public async Task GetPostProcessSteps_StoredIdWithNoCodeDefault_IsIgnored()
        {
            var svc = NewService();

            await svc.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create("retired-step", enabled: true, new { }),
            });

            var steps = await NewService().GetPostProcessStepsAsync();

            Assert.DoesNotContain(steps, s => s.StepId == "retired-step");
            Assert.Equal(
                new[] { AudioPostProcessStepIds.SilenceTrim, AudioPostProcessStepIds.ConsonantSoften },
                steps.Select(s => s.StepId));
        }

        [Fact]
        public async Task GetPostProcessSteps_RowPredatingSilenceTrim_GainsItEnabled()
        {
            // The migration-free upgrade path: rows written before silence-trim existed carry a
            // consonant-soften entry only, and must come back with silence-trim on by default.
            var svc = NewService();
            await svc.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create(
                    AudioPostProcessStepIds.ConsonantSoften, enabled: true,
                    new ConsonantSoftenSettings { Preset = ConsonantSoftenPresets.Medium }),
            });

            var steps = await NewService().GetPostProcessStepsAsync();

            var trim = steps.Single(s => s.StepId == AudioPostProcessStepIds.SilenceTrim);
            Assert.True(trim.Enabled);
            Assert.Equal(50, trim.GetSettings<SilenceTrimSettings>()!.PadMs);

            var soften = steps.Single(s => s.StepId == AudioPostProcessStepIds.ConsonantSoften);
            Assert.True(soften.Enabled);
            Assert.Equal(ConsonantSoftenPresets.Medium, soften.GetSettings<ConsonantSoftenSettings>()!.Preset);
        }

        [Fact]
        public async Task GetPostProcessSteps_StoredEntryWithoutSettings_KeepsEnabled_TakesDefaultSettings()
        {
            var svc = NewService();
            await svc.SetPostProcessStepsAsync(new[]
            {
                new AudioPostProcessStepConfig(AudioPostProcessStepIds.SilenceTrim, Enabled: false, Settings: null),
            });

            var trim = (await NewService().GetPostProcessStepsAsync())
                .Single(s => s.StepId == AudioPostProcessStepIds.SilenceTrim);

            Assert.False(trim.Enabled);
            Assert.Equal(-50, trim.GetSettings<SilenceTrimSettings>()!.ThresholdDb);
        }

        [Fact]
        public async Task SetPostProcessSteps_CustomParams_RoundTrip()
        {
            var svc = NewService();
            var config = AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, enabled: true,
                new ConsonantSoftenSettings
                {
                    Engine = ConsonantSoftenEngines.AdynEq,
                    Preset = ConsonantSoftenPresets.Custom,
                    AdynEq = new AdynEqParams { ThresholdDb = -28, Ratio = 3, HighpassHz = 80 },
                });

            await svc.SetPostProcessStepsAsync(new[] { config });

            var settings = (await NewService().GetPostProcessStepsAsync())
                .Single(s => s.StepId == AudioPostProcessStepIds.ConsonantSoften)
                .GetSettings<ConsonantSoftenSettings>();
            Assert.NotNull(settings?.AdynEq);
            Assert.Equal(-28, settings.AdynEq.ThresholdDb);
            Assert.Equal(3, settings.AdynEq.Ratio);
            Assert.Equal(80, settings.AdynEq.HighpassHz);
        }

        [Fact]
        public async Task GetPostProcessSteps_CorruptJson_ReturnsDefaults()
        {
            var svc = NewService();
            await svc.SetFfmpegPathAsync("x"); // creates the settings row
            await using (var db = await Factory.CreateDbContextAsync())
            {
                var row = await db.Settings.SingleAsync();
                row.AudioPostProcessStepsJson = "{not json";
                await db.SaveChangesAsync();
            }

            var steps = await svc.GetPostProcessStepsAsync();

            Assert.Equal(
                new[] { AudioPostProcessStepIds.SilenceTrim, AudioPostProcessStepIds.ConsonantSoften },
                steps.Select(s => s.StepId));
            Assert.True(steps[0].Enabled);
            Assert.False(steps[1].Enabled);
        }

        [Fact]
        public async Task SetPostProcessSteps_RaisesOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create(AudioPostProcessStepIds.ConsonantSoften, enabled: true, new ConsonantSoftenSettings()),
            });

            Assert.Equal(1, count);
        }
    }
}
