using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class DePlosiveChainBuilderTests
    {
        [Fact]
        public void Default_cuts_at_60Hz_at_order_10()
        {
            Assert.StartsWith("asubcut=cutoff=60:order=10", DePlosiveChainBuilder.Build(null));
        }

        [Fact]
        public void Always_emits_the_limiter_tail()
        {
            // asubcut is cut-only and still regrows true peak to +0.000265 dB on loudnorm'd input.
            // Without the tail this step ships clipping — the tail is mandatory, not cosmetic.
            Assert.EndsWith(DePlosiveChainBuilder.LimiterTail, DePlosiveChainBuilder.Build(new DePlosiveSettings(80)));
        }

        [Theory]
        [InlineData(10, 40)]
        [InlineData(40, 40)]
        [InlineData(90, 90)]
        [InlineData(120, 120)]
        [InlineData(400, 120)]
        public void Cutoff_clamps_to_the_40_to_120_band(double requested, double expected)
        {
            Assert.Contains($"cutoff={expected:0}", DePlosiveChainBuilder.Build(new DePlosiveSettings(requested)));
        }
    }
}
