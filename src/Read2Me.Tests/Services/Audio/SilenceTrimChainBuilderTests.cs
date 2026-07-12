using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class SilenceTrimChainBuilderTests
    {
        [Fact]
        public void NullSettings_UsesDefaults()
        {
            var chain = SilenceTrimChainBuilder.Build(null);

            Assert.Contains("start_threshold=-50dB", chain);
            Assert.Contains("start_silence=0.05", chain);
        }

        [Fact]
        public void ReverseSandwich_TrimsBothEnds()
        {
            var chain = SilenceTrimChainBuilder.Build(new SilenceTrimSettings());

            var parts = chain.Split(", ");
            Assert.Equal(4, parts.Length);
            Assert.StartsWith("silenceremove=", parts[0]);
            Assert.Equal("areverse", parts[1]);
            Assert.StartsWith("silenceremove=", parts[2]);
            Assert.Equal("areverse", parts[3]);
            Assert.Equal(parts[0], parts[2]);
        }

        [Fact]
        public void NeverStripsMidClipSilence()
        {
            var chain = SilenceTrimChainBuilder.Build(new SilenceTrimSettings());

            Assert.DoesNotContain("stop_periods", chain);
        }

        [Fact]
        public void HeadTrimIsSinglePeriodWithZeroDuration()
        {
            var chain = SilenceTrimChainBuilder.Build(new SilenceTrimSettings());

            Assert.Contains("start_periods=1", chain);
            Assert.Contains("start_duration=0", chain);
            Assert.Contains("detection=peak", chain);
        }

        [Fact]
        public void PadZero_OmitsStartSilence_ForAHardTrim()
        {
            var chain = SilenceTrimChainBuilder.Build(new SilenceTrimSettings(PadMs: 0));

            Assert.DoesNotContain("start_silence", chain);
        }

        [Theory]
        [InlineData(50, "0.05")]
        [InlineData(200, "0.2")]
        [InlineData(1500, "1.5")]
        public void PadMs_ConvertsToSeconds(int padMs, string expected)
        {
            var chain = SilenceTrimChainBuilder.Build(new SilenceTrimSettings(PadMs: padMs));

            Assert.Contains($"start_silence={expected}", chain);
        }

        [Theory]
        [InlineData(-60, "-60")]
        [InlineData(-42.5, "-42.5")]
        [InlineData(0, "0")]
        public void ThresholdDb_FormattedInvariantWithDbSuffix(double thresholdDb, string expected)
        {
            var chain = SilenceTrimChainBuilder.Build(new SilenceTrimSettings(ThresholdDb: thresholdDb));

            Assert.Contains($"start_threshold={expected}dB", chain);
        }
    }
}
