using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ConsonantSoftenStepTests
    {
        private static ConsonantSoftenStep NewStep() =>
            new(NullLogger<ConsonantSoftenStep>.Instance);

        private static string BogusFfmpeg() =>
            Path.Combine(Path.GetTempPath(), $"definitely-not-ffmpeg-{Guid.NewGuid():N}.exe");

        [Fact]
        public void StepId_IsConsonantSoften()
        {
            Assert.Equal(AudioPostProcessStepIds.ConsonantSoften, NewStep().StepId);
        }

        [Fact]
        public async Task MissingFfmpeg_ReturnsInputUnchanged_NotApplied_WithReason()
        {
            var input = new byte[] { 1, 2, 3, 4, 5, 42, 99 };

            var result = await NewStep().ProcessAsync(input, BogusFfmpeg(), settingsJson: null, CancellationToken.None);

            Assert.False(result.Applied);
            Assert.NotNull(result.Reason);
            Assert.Equal(input, result.Audio);
        }

        [Fact]
        public async Task MissingFfmpeg_DoesNotThrow()
        {
            var input = new byte[] { 9, 9, 9 };

            var ex = await Record.ExceptionAsync(() =>
                NewStep().ProcessAsync(input, BogusFfmpeg(), settingsJson: null, CancellationToken.None));

            Assert.Null(ex);
        }

        [Fact]
        public async Task MalformedSettingsJson_StillFallsBack_WithoutThrowing()
        {
            var input = new byte[] { 7, 7, 7 };

            var result = await NewStep().ProcessAsync(input, BogusFfmpeg(), settingsJson: "{ not valid", CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal(input, result.Audio);
        }

        [Fact]
        public async Task ValidSettingsJson_ButMissingFfmpeg_FallsBack()
        {
            var settings = new ConsonantSoftenSettings
            {
                Engine = ConsonantSoftenEngines.Deesser,
                Preset = ConsonantSoftenPresets.Medium,
            };
            var json = JsonSerializer.Serialize(settings, AudioPostProcessJson.Options);
            var input = new byte[] { 4, 5, 6 };

            var result = await NewStep().ProcessAsync(input, BogusFfmpeg(), json, CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal(input, result.Audio);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NewStep().ProcessAsync(new byte[] { 1 }, BogusFfmpeg(), null, cts.Token));
        }
    }
}
