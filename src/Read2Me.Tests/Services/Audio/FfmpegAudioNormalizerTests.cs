using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class FfmpegAudioNormalizerTests
    {
        private FfmpegAudioNormalizer NewNormalizer() =>
            new(NullLogger<FfmpegAudioNormalizer>.Instance);

        [Fact]
        public async Task BogusPath_ReturnsSkipped_WithOriginalBytesIntact_AndThrowsNothing()
        {
            var originalBytes = new byte[] { 1, 2, 3, 4, 5, 42, 99 };
            using var input = new MemoryStream(originalBytes);
            var bogusPath = Path.Combine(Path.GetTempPath(), $"definitely-not-ffmpeg-{Guid.NewGuid():N}.exe");

            var result = await NewNormalizer().NormalizeAsync(input, bogusPath);

            Assert.Equal(NormalizeStatus.Skipped, result.Status);
            Assert.Equal("ffmpeg not found (set path in Audio Processing settings)", result.Reason);

            using var ms = new MemoryStream();
            await result.Audio.CopyToAsync(ms);
            Assert.Equal(originalBytes, ms.ToArray());
        }

        [Fact]
        public async Task SkippedAudio_IsSeekableAndRewound()
        {
            var originalBytes = new byte[] { 10, 20, 30 };
            using var input = new MemoryStream(originalBytes);
            var bogusPath = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.exe");

            var result = await NewNormalizer().NormalizeAsync(input, bogusPath);

            Assert.True(result.Audio.CanSeek);
            Assert.Equal(0, result.Audio.Position);
        }

        // --- NormalizeToWavAsync contract ---

        [Fact]
        public async Task NormalizeToWavAsync_AbsentFfmpeg_Throws_WithFfmpegPathMessage()
        {
            var input = new MemoryStream(new byte[] { 1, 2, 3 });
            var bogusPath = Path.Combine(Path.GetTempPath(), $"definitely-not-ffmpeg-{Guid.NewGuid():N}.exe");

            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                NewNormalizer().NormalizeToWavAsync(input, bogusPath));

            Assert.Contains("ffmpeg", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task NormalizeToWavAsync_AbsentFfmpeg_Throws_WhileNormalizeAsync_ReturnsSkipped()
        {
            // Asserts the key contract difference: same bogus path, opposite outcomes.
            var bogusPath = Path.Combine(Path.GetTempPath(), $"definitely-not-ffmpeg-{Guid.NewGuid():N}.exe");

            // NormalizeAsync → Skipped (never throws)
            var normalizeResult = await NewNormalizer().NormalizeAsync(
                new MemoryStream(new byte[] { 1, 2, 3 }), bogusPath);
            Assert.Equal(NormalizeStatus.Skipped, normalizeResult.Status);

            // NormalizeToWavAsync → throws
            await Assert.ThrowsAnyAsync<Exception>(() =>
                NewNormalizer().NormalizeToWavAsync(
                    new MemoryStream(new byte[] { 1, 2, 3 }), bogusPath));
        }
    }
}
