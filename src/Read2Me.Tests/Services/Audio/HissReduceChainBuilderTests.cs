using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class HissReduceChainBuilderTests
    {
        [Fact]
        public void Light_is_the_default_and_emits_its_exact_band_profile()
        {
            Assert.Equal(
                "afftdn=nr=12:nt=custom:bn=-20 -20 -20 -20 -20 -20 -20 -10 0 5 10 15 20 20 20",
                HissReduceChainBuilder.Build(null));
        }

        [Fact]
        public void Strong_emits_its_exact_band_profile()
        {
            Assert.Equal(
                "afftdn=nr=30:nt=custom:bn=-40 -40 -40 -40 -40 -40 -30 -20 -5 5 15 25 30 30 30",
                HissReduceChainBuilder.Build(new HissReduceSettings { Preset = HissReducePresets.Strong }));
        }

        [Fact]
        public void Unknown_preset_falls_back_to_light()
        {
            Assert.Equal(
                HissReduceChainBuilder.Build(new HissReduceSettings { Preset = HissReducePresets.Light }),
                HissReduceChainBuilder.Build(new HissReduceSettings { Preset = "nonsense" }));
        }

        [Fact]
        public void Emits_no_limiter_tail()
        {
            Assert.DoesNotContain("alimiter", HissReduceChainBuilder.Build(null));
        }

        [Theory]
        [InlineData(48000)]
        [InlineData(16000)]
        public void Rejects_audio_that_is_not_24kHz(int sampleRateHz)
        {
            // bn's 15 bands are indexed relative to Nyquist, not Hz: at another rate the profile is
            // silently aimed at the wrong frequencies. Fail loudly instead.
            Assert.Throws<ArgumentOutOfRangeException>(() => HissReduceChainBuilder.Build(null, sampleRateHz));
        }
    }
}
