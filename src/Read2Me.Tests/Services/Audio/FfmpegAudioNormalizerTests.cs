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

        // --- Canonical output format ---

        [Fact]
        public async Task NormalizeAsync_Downsamples_To_CanonicalRate()
        {
            // loudnorm runs at an internal 192 kHz; without format args the stored WAV inherits it.
            if (!FfmpegOnPath())
                Assert.Skip("ffmpeg not on PATH");

            using var input = new MemoryStream(SineWav(sampleRate: 48000, seconds: 1));

            var result = await NewNormalizer().NormalizeAsync(input, ffmpegPath: null);

            Assert.Equal(NormalizeStatus.Normalized, result.Status);

            using var ms = new MemoryStream();
            await result.Audio.CopyToAsync(ms);
            var wav = ms.ToArray();

            Assert.Equal(1, BitConverter.ToInt16(wav, 22));      // channels
            Assert.Equal(24000, BitConverter.ToInt32(wav, 24));  // sample rate
            Assert.Equal(16, BitConverter.ToInt16(wav, 34));     // bits per sample
        }

        private static bool FfmpegOnPath()
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
                p!.WaitForExit();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] SineWav(int sampleRate, int seconds)
        {
            var samples = sampleRate * seconds;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);

            w.Write("RIFF"u8.ToArray());
            w.Write(36 + samples * 2);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);
            w.Write((short)1);              // PCM
            w.Write((short)1);              // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);        // byte rate
            w.Write((short)2);              // block align
            w.Write((short)16);             // bits
            w.Write("data"u8.ToArray());
            w.Write(samples * 2);

            for (var i = 0; i < samples; i++)
                w.Write((short)(short.MaxValue * 0.5 * Math.Sin(2 * Math.PI * 440 * i / sampleRate)));

            w.Flush();
            return ms.ToArray();
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
