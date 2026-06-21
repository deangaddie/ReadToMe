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

            var (ffmpegPath, werThreshold) = await svc.GetAsync();

            Assert.Null(ffmpegPath);
            Assert.Equal(0.15, werThreshold);
        }

        [Fact]
        public async Task SetFfmpegPath_Persists()
        {
            var svc = NewService();

            await svc.SetFfmpegPathAsync(@"C:\tools\ffmpeg.exe");

            var (ffmpegPath, _) = await NewService().GetAsync();
            Assert.Equal(@"C:\tools\ffmpeg.exe", ffmpegPath);
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

            var (ffmpegPath, _) = await NewService().GetAsync();
            Assert.Null(ffmpegPath);
        }

        [Fact]
        public async Task SetWerThreshold_Persists()
        {
            var svc = NewService();

            await svc.SetWerThresholdAsync(0.42);

            var (_, werThreshold) = await NewService().GetAsync();
            Assert.Equal(0.42, werThreshold);
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
    }
}
