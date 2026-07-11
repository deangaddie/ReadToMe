using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ConsonantSoftenPresetsTests
    {
        [Theory]
        [InlineData(ConsonantSoftenPresets.Light, -20, 2, 6)]
        [InlineData(ConsonantSoftenPresets.Medium, -26, 4, 12)]
        [InlineData(ConsonantSoftenPresets.Strong, -34, 6, 15)]
        public void AdynEq_PresetLadder_ResolvesPerSpec(string preset, double thresholdDb, double ratio, double rangeDb)
        {
            var p = ConsonantSoftenPresets.ResolveAdynEq(preset);

            Assert.Equal(thresholdDb, p.ThresholdDb);
            Assert.Equal(ratio, p.Ratio);
            Assert.Equal(rangeDb, p.RangeDb);
            Assert.Equal(-3, p.ShelfGainDb);
        }

        [Fact]
        public void AdynEq_CommonParams_ResolvePerSpec()
        {
            var p = ConsonantSoftenPresets.ResolveAdynEq(ConsonantSoftenPresets.Strong);

            Assert.Equal(6000, p.DetectFrequencyHz);
            Assert.Equal(0.7, p.DetectQ);
            Assert.Equal(6000, p.TargetFrequencyHz);
            Assert.Equal(0.7, p.TargetQ);
            Assert.Equal(5, p.AttackMs);
            Assert.Equal(60, p.ReleaseMs);
            Assert.Equal(6500, p.ShelfFrequencyHz);
            Assert.Null(p.HighpassHz);
        }

        [Theory]
        [InlineData(ConsonantSoftenPresets.Light, 0.35, 0.5)]
        [InlineData(ConsonantSoftenPresets.Medium, 0.5, 0.5)]
        [InlineData(ConsonantSoftenPresets.Strong, 0.7, 0.7)]
        public void Deesser_PresetLadder_ResolvesPerSpec(string preset, double intensity, double amount)
        {
            var p = ConsonantSoftenPresets.ResolveDeesser(preset);

            Assert.Equal(intensity, p.Intensity);
            Assert.Equal(amount, p.MakeupAmount);
            Assert.Equal(0.5, p.Frequency);
            Assert.Equal(6500, p.ShelfFrequencyHz);
            Assert.Equal(-3, p.ShelfGainDb);
            Assert.Null(p.HighpassHz);
        }

        [Theory]
        [InlineData(ConsonantSoftenPresets.Custom)]
        [InlineData("unknown-preset")]
        public void UnknownOrCustomPreset_ResolvesToStrong(string preset)
        {
            var adynEq = ConsonantSoftenPresets.ResolveAdynEq(preset);
            var deesser = ConsonantSoftenPresets.ResolveDeesser(preset);

            Assert.Equal(-34, adynEq.ThresholdDb);
            Assert.Equal(0.7, deesser.Intensity);
        }
    }
}
