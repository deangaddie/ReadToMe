using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class SilenceTrimStepTests
    {
        private static SilenceTrimStep NewStep() => new(NullLogger<SilenceTrimStep>.Instance);

        private static string BogusFfmpeg() =>
            Path.Combine(Path.GetTempPath(), $"definitely-not-ffmpeg-{Guid.NewGuid():N}.exe");

        [Fact]
        public void StepId_IsSilenceTrim()
        {
            Assert.Equal(AudioPostProcessStepIds.SilenceTrim, NewStep().StepId);
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
            var ex = await Record.ExceptionAsync(() =>
                NewStep().ProcessAsync([9, 9, 9], BogusFfmpeg(), settingsJson: null, CancellationToken.None));

            Assert.Null(ex);
        }

        [Fact]
        public async Task MalformedSettingsJson_StillFallsBack_WithoutThrowing()
        {
            var input = new byte[] { 7, 7, 7 };

            var result = await NewStep().ProcessAsync(input, BogusFfmpeg(), "{ not valid", CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal(input, result.Audio);
        }

        [Fact]
        public async Task ValidSettingsJson_ButMissingFfmpeg_FallsBack()
        {
            var json = JsonSerializer.Serialize(
                new SilenceTrimSettings(ThresholdDb: -40, PadMs: 0), AudioPostProcessJson.Options);
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
                NewStep().ProcessAsync([1], BogusFfmpeg(), null, cts.Token));
        }
    }

    /// <summary>
    /// Real-ffmpeg tests — the trim itself and the trimmed-to-nothing guard can only be observed
    /// against the actual filter. Skipped when ffmpeg is not on PATH, per the repo's existing
    /// ffmpeg-gated pattern.
    /// </summary>
    public class SilenceTrimStepIntegrationTests
    {
        private static SilenceTrimStep NewStep() => new(NullLogger<SilenceTrimStep>.Instance);

        private static bool FfmpegAvailable()
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                p?.WaitForExit(3000);
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>Canonical WAV (24 kHz mono 16-bit): silence, then a tone, then silence.</summary>
        private static byte[] Wav(int leadingSilenceMs, int toneMs, int trailingSilenceMs)
        {
            var samples = new List<short>();
            void Silence(int ms) => samples.AddRange(new short[Samples(ms)]);
            void Tone(int ms)
            {
                for (int i = 0; i < Samples(ms); i++)
                    samples.Add((short)(short.MaxValue * 0.5 *
                        Math.Sin(2 * Math.PI * 440 * i / CanonicalWav.SampleRateHz)));
            }

            Silence(leadingSilenceMs);
            Tone(toneMs);
            Silence(trailingSilenceMs);

            using var ms2 = new MemoryStream();
            using var bw = new BinaryWriter(ms2);
            int dataBytes = samples.Count * CanonicalWav.BytesPerSample;

            bw.Write("RIFF"u8.ToArray());
            bw.Write(36 + dataBytes);
            bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray());
            bw.Write(16);
            bw.Write((short)1);                              // PCM
            bw.Write((short)1);                              // mono
            bw.Write(CanonicalWav.SampleRateHz);
            bw.Write(CanonicalWav.BytesPerSecond);
            bw.Write((short)CanonicalWav.BytesPerSample);
            bw.Write((short)16);
            bw.Write("data"u8.ToArray());
            bw.Write(dataBytes);
            foreach (var s in samples) bw.Write(s);

            bw.Flush();
            return ms2.ToArray();

            static int Samples(int ms) => CanonicalWav.SampleRateHz * ms / 1000;
        }

        [Fact]
        public async Task TrimsDeadAirFromBothEnds_KeepingTheSpeechAndThePad()
        {
            if (!FfmpegAvailable()) return;

            var input = Wav(leadingSilenceMs: 2000, toneMs: 1000, trailingSilenceMs: 2000);

            var result = await NewStep().ProcessAsync(input, null, null, CancellationToken.None);

            Assert.True(result.Applied);
            Assert.Null(result.Reason);
            // 1 s tone + a 50 ms pad at each end, give or take the filter's sample-level edges.
            Assert.InRange(CanonicalWav.DurationMs(result.Audio.Length), 1050, 1200);
        }

        [Fact]
        public async Task ShortItemAfterLongDeadAir_IsTrimmed_NotSkipped()
        {
            if (!FfmpegAvailable()) return;

            // The case the absolute-floor guard exists to protect: a legitimate trim removing
            // ~90% of the clip.
            var input = Wav(leadingSilenceMs: 2000, toneMs: 300, trailingSilenceMs: 2000);

            var result = await NewStep().ProcessAsync(input, null, null, CancellationToken.None);

            Assert.True(result.Applied);
            Assert.InRange(CanonicalWav.DurationMs(result.Audio.Length), 350, 500);
        }

        [Fact]
        public async Task AllSilence_GuardFires_ReturningTheInputUnchanged()
        {
            if (!FfmpegAvailable()) return;

            var input = Wav(leadingSilenceMs: 2000, toneMs: 0, trailingSilenceMs: 0);

            var result = await NewStep().ProcessAsync(input, null, null, CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal("trim would remove nearly all audio", result.Reason);
            Assert.Equal(input, result.Audio);
        }
    }
}
