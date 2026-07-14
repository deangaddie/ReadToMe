using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class DenoiseStepTests
    {
        private static DenoiseStep NewStep() => new(NullLogger<DenoiseStep>.Instance);

        [Fact]
        public void StepId_IsDenoise()
        {
            Assert.Equal(AudioPostProcessStepIds.Denoise, NewStep().StepId);
        }

        [Fact]
        public async Task MissingFfmpeg_ReturnsInputUnchanged_NotApplied_WithReason()
        {
            var input = new byte[] { 1, 2, 3, 4, 5 };

            var result = await NewStep().ProcessAsync(
                input, TestFfmpeg.BogusPath(), settingsJson: null, CancellationToken.None);

            Assert.False(result.Applied);
            Assert.NotNull(result.Reason);
            Assert.Equal(input, result.Audio);
        }

        [Fact]
        public async Task MalformedSettingsJson_StillFallsBack_WithoutThrowing()
        {
            var input = new byte[] { 7, 7, 7 };

            var result = await NewStep().ProcessAsync(
                input, TestFfmpeg.BogusPath(), "{ not valid", CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal(input, result.Audio);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NewStep().ProcessAsync([1], TestFfmpeg.BogusPath(), null, cts.Token));
        }
    }

    /// <summary>
    /// Real-ffmpeg round-trip: the filter itself can only be observed against the actual binary.
    /// Silently no-ops when ffmpeg is absent, per the repo's ffmpeg-gated pattern.
    /// </summary>
    public class DenoiseStepIntegrationTests
    {
        [Fact]
        public async Task KeepsTheLength()
        {
            if (!TestFfmpeg.Available()) return;

            var input = TestWav.Tone(1000);
            var step = new DenoiseStep(NullLogger<DenoiseStep>.Instance);

            var result = await step.ProcessAsync(input, null, null, CancellationToken.None);

            Assert.True(result.Applied);
            Assert.InRange(CanonicalWav.DurationMs(result.Audio.Length), 950, 1050);
        }
    }
}
