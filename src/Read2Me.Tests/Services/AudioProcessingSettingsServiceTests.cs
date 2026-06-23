using System.Threading;
using System.Threading.Tasks;
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
        public async Task Get_MissingRow_ReturnsSentenceChunkingDefaults()
        {
            var svc = NewService();

            var settings = await svc.GetAsync();

            Assert.True(settings.SentenceSplitEnabled);
            Assert.Equal(300, settings.SentencePauseMs);
            Assert.Equal(15, settings.SentenceMinChunkChars);
        }

        [Fact]
        public async Task SetSentenceChunking_RoundTrips()
        {
            var svc = NewService();

            await svc.SetSentenceChunkingAsync(enabled: false, pauseMs: 750, minChunkChars: 40);

            var settings = await NewService().GetAsync();
            Assert.False(settings.SentenceSplitEnabled);
            Assert.Equal(750, settings.SentencePauseMs);
            Assert.Equal(40, settings.SentenceMinChunkChars);
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
    }
}
