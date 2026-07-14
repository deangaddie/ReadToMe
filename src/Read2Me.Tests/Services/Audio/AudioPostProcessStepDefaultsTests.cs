using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioPostProcessStepDefaultsTests
    {
        [Fact]
        public void Voice_scope_is_the_five_steps_in_their_fixed_order()
        {
            // Order is forced by real interactions (denoise before trim, or the threshold detector
            // reads the noise floor as signal), which is why the editor is a checklist, not a builder.
            Assert.Equal(
                [
                    AudioPostProcessStepIds.DePlosive,
                    AudioPostProcessStepIds.Denoise,
                    AudioPostProcessStepIds.HissReduce,
                    AudioPostProcessStepIds.ConsonantSoften,
                    AudioPostProcessStepIds.SilenceTrim,
                ],
                AudioPostProcessStepDefaults.For(StepScope.Voice).Select(s => s.StepId));
        }

        [Fact]
        public void Paragraph_scope_never_offers_the_voice_only_steps()
        {
            // Without this they would appear as cards on the Audio Processing settings page and join
            // the paragraph pipeline.
            var ids = AudioPostProcessStepDefaults.For(StepScope.Paragraph).Select(s => s.StepId).ToList();

            Assert.Equal([AudioPostProcessStepIds.SilenceTrim, AudioPostProcessStepIds.ConsonantSoften], ids);
        }

        [Fact]
        public void Same_step_carries_different_defaults_in_each_scope()
        {
            var paragraph = Trim(StepScope.Paragraph);
            var voice = Trim(StepScope.Voice);

            // -50 dB removes 0 ms from a mic clip (detection=peak), so voice trims at -35; and a
            // reference voice trimmed to under a second has gone wrong, unlike a one-word item.
            Assert.Equal(-50, paragraph.ThresholdDb);
            Assert.Equal(200, paragraph.MinOutputMs);
            Assert.Equal(-35, voice.ThresholdDb);
            Assert.Equal(1000, voice.MinOutputMs);
        }

        [Fact]
        public void Voice_defaults_are_the_gentlest_setting_on_every_ladder()
        {
            var soften = Step(StepScope.Voice, AudioPostProcessStepIds.ConsonantSoften)
                .GetSettings<ConsonantSoftenSettings>()!;
            var hiss = Step(StepScope.Voice, AudioPostProcessStepIds.HissReduce)
                .GetSettings<HissReduceSettings>()!;

            Assert.Equal(ConsonantSoftenPresets.Light, soften.Preset);
            Assert.Equal(HissReducePresets.Light, hiss.Preset);
            Assert.Equal(DePlosiveSettings.DefaultCutoffHz,
                Step(StepScope.Voice, AudioPostProcessStepIds.DePlosive).GetSettings<DePlosiveSettings>()!.CutoffHz);
            Assert.Equal(DenoiseSettings.DefaultStrength,
                Step(StepScope.Voice, AudioPostProcessStepIds.Denoise).GetSettings<DenoiseSettings>()!.Strength);
        }

        [Fact]
        public void Paragraph_soften_stays_strong()
        {
            var soften = Step(StepScope.Paragraph, AudioPostProcessStepIds.ConsonantSoften)
                .GetSettings<ConsonantSoftenSettings>()!;

            Assert.Equal(ConsonantSoftenPresets.Strong, soften.Preset);
        }

        private static AudioPostProcessStepConfig Step(StepScope scope, string stepId) =>
            AudioPostProcessStepDefaults.For(scope).Single(s => s.StepId == stepId);

        private static SilenceTrimSettings Trim(StepScope scope) =>
            Step(scope, AudioPostProcessStepIds.SilenceTrim).GetSettings<SilenceTrimSettings>()!;
    }

    public class SilenceTrimGuardTests
    {
        [Fact]
        public async Task MinOutputMs_comes_from_settings_so_the_guard_is_per_scope()
        {
            if (!FfmpegAvailable()) return;

            // 900 ms of speech survives the paragraph guard (200 ms) and trips the voice guard (1000).
            var input = Wav(leadingSilenceMs: 1000, toneMs: 900, trailingSilenceMs: 1000);
            var step = new SilenceTrimStep(NullLogger<SilenceTrimStep>.Instance);

            var paragraph = await step.ProcessAsync(
                input, null, Json(new SilenceTrimSettings(ThresholdDb: -50, PadMs: 0, MinOutputMs: 200)),
                CancellationToken.None);
            var voice = await step.ProcessAsync(
                input, null, Json(new SilenceTrimSettings(ThresholdDb: -50, PadMs: 0, MinOutputMs: 1000)),
                CancellationToken.None);

            Assert.True(paragraph.Applied);
            Assert.False(voice.Applied);
            Assert.Equal("trim would remove nearly all audio", voice.Reason);
            Assert.Equal(input, voice.Audio);
        }

        private static string Json(SilenceTrimSettings settings) =>
            System.Text.Json.JsonSerializer.Serialize(settings, AudioPostProcessJson.Options);

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

        private static byte[] Wav(int leadingSilenceMs, int toneMs, int trailingSilenceMs)
        {
            var samples = new List<short>();
            samples.AddRange(new short[Count(leadingSilenceMs)]);
            for (var i = 0; i < Count(toneMs); i++)
                samples.Add((short)(short.MaxValue * 0.5 *
                    Math.Sin(2 * Math.PI * 440 * i / CanonicalWav.SampleRateHz)));
            samples.AddRange(new short[Count(trailingSilenceMs)]);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            var dataBytes = samples.Count * CanonicalWav.BytesPerSample;

            bw.Write("RIFF"u8.ToArray());
            bw.Write(36 + dataBytes);
            bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray());
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(CanonicalWav.SampleRateHz);
            bw.Write(CanonicalWav.BytesPerSecond);
            bw.Write((short)CanonicalWav.BytesPerSample);
            bw.Write((short)16);
            bw.Write("data"u8.ToArray());
            bw.Write(dataBytes);
            foreach (var s in samples) bw.Write(s);

            bw.Flush();
            return ms.ToArray();

            static int Count(int ms) => CanonicalWav.SampleRateHz * ms / 1000;
        }
    }
}
