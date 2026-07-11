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
        public async Task GetPostProcessSteps_MissingRow_ReturnsDisabledConsonantSoftenDefault()
        {
            var svc = NewService();

            var steps = await svc.GetPostProcessStepsAsync();

            var step = Assert.Single(steps);
            Assert.Equal(AudioPostProcessStepIds.ConsonantSoften, step.StepId);
            Assert.False(step.Enabled);
            var settings = step.GetSettings<ConsonantSoftenSettings>();
            Assert.NotNull(settings);
            Assert.Equal(ConsonantSoftenEngines.AdynEq, settings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Strong, settings.Preset);
        }

        [Fact]
        public async Task SetPostProcessSteps_PresetRef_RoundTrips()
        {
            var svc = NewService();
            var config = AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, enabled: true,
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.Deesser, Preset = ConsonantSoftenPresets.Light });

            await svc.SetPostProcessStepsAsync(new[] { config });

            var steps = await NewService().GetPostProcessStepsAsync();
            var step = Assert.Single(steps);
            Assert.True(step.Enabled);
            var settings = step.GetSettings<ConsonantSoftenSettings>();
            Assert.NotNull(settings);
            Assert.Equal(ConsonantSoftenEngines.Deesser, settings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Light, settings.Preset);
            Assert.Null(settings.AdynEq);
            Assert.Null(settings.Deesser);
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

            var settings = Assert.Single(await NewService().GetPostProcessStepsAsync())
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

            var step = Assert.Single(steps);
            Assert.Equal(AudioPostProcessStepIds.ConsonantSoften, step.StepId);
            Assert.False(step.Enabled);
        }

        [Fact]
        public async Task GetPostProcessSteps_EntryMissingFromStoredList_AppendsDisabledDefault()
        {
            var svc = NewService();
            await svc.SetPostProcessStepsAsync(new[]
            {
                AudioPostProcessStepConfig.Create("some-other-step", enabled: true, new ConsonantSoftenSettings()),
            });

            var steps = await NewService().GetPostProcessStepsAsync();

            Assert.Equal(2, steps.Count);
            var soften = steps.Single(s => s.StepId == AudioPostProcessStepIds.ConsonantSoften);
            Assert.False(soften.Enabled);
            Assert.Equal(ConsonantSoftenPresets.Strong, soften.GetSettings<ConsonantSoftenSettings>()!.Preset);
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
