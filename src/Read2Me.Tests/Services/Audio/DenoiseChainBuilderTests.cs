using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class DenoiseChainBuilderTests
    {
        [Fact]
        public void Default_is_anlmdn_at_strength_20()
        {
            Assert.Equal("anlmdn=s=20", DenoiseChainBuilder.Build(null));
        }

        [Fact]
        public void Never_uses_afftdn()
        {
            // afftdn eats 1.73 dB of the 4-12 kHz air band at equal attenuation vs anlmdn's 0.18 —
            // it *is* the "watery" failure mode, and its real knob would force a probe pass.
            Assert.DoesNotContain("afftdn", DenoiseChainBuilder.Build(new DenoiseSettings(500)));
        }

        [Fact]
        public void Emits_no_limiter_tail()
        {
            // anlmdn is peak-safe. A limiter here would be a bug, not a belt-and-braces.
            Assert.DoesNotContain("alimiter", DenoiseChainBuilder.Build(new DenoiseSettings(200)));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(500, 500)]
        [InlineData(5000, 1000)]
        public void Strength_clamps_to_the_1_to_1000_band(double requested, double expected)
        {
            Assert.Equal($"anlmdn=s={expected:0}", DenoiseChainBuilder.Build(new DenoiseSettings(requested)));
        }
    }
}
