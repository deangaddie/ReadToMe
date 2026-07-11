using System.Globalization;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ConsonantSoftenChainBuilderTests
    {
        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        // --- Limiter tail (mandatory, hidden, on every chain) ---

        [Theory]
        [InlineData(ConsonantSoftenEngines.AdynEq, ConsonantSoftenPresets.Light)]
        [InlineData(ConsonantSoftenEngines.AdynEq, ConsonantSoftenPresets.Medium)]
        [InlineData(ConsonantSoftenEngines.AdynEq, ConsonantSoftenPresets.Strong)]
        [InlineData(ConsonantSoftenEngines.Deesser, ConsonantSoftenPresets.Light)]
        [InlineData(ConsonantSoftenEngines.Deesser, ConsonantSoftenPresets.Medium)]
        [InlineData(ConsonantSoftenEngines.Deesser, ConsonantSoftenPresets.Strong)]
        public void EveryChain_EndsWith_MandatoryLimiter(string engine, string preset)
        {
            var chain = ConsonantSoftenChainBuilder.Build(
                new ConsonantSoftenSettings { Engine = engine, Preset = preset });

            Assert.EndsWith("alimiter=limit=0.841:level=false", chain);
        }

        // --- adynEQ engine ---

        [Fact]
        public void AdynEq_Strong_EmitsSpecFilter()
        {
            var chain = ConsonantSoftenChainBuilder.Build(
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.AdynEq, Preset = ConsonantSoftenPresets.Strong });

            // Strong: threshold -34 dB → 10^(-34/20) ≈ 0.01995
            Assert.Contains("adynamicequalizer=threshold=", chain);
            Assert.Contains("dfrequency=6000:dqfactor=0.7:tfrequency=6000:tqfactor=0.7", chain);
            Assert.Contains("attack=5:release=60", chain);
            Assert.Contains("ratio=6", chain);
            Assert.Contains("range=15", chain);
            Assert.Contains("mode=cutabove:auto=off", chain);
            Assert.Contains("treble=f=6500:t=q:w=0.707:g=-3", chain);
        }

        [Theory]
        [InlineData(ConsonantSoftenPresets.Light, -20, 2, 6)]
        [InlineData(ConsonantSoftenPresets.Medium, -26, 4, 12)]
        [InlineData(ConsonantSoftenPresets.Strong, -34, 6, 15)]
        public void AdynEq_PresetLadder_MapsToFilterParams(string preset, double thresholdDb, double ratio, double rangeDb)
        {
            var chain = ConsonantSoftenChainBuilder.Build(
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.AdynEq, Preset = preset });

            var lin = Math.Pow(10, thresholdDb / 20.0);
            Assert.Contains($"threshold={F(lin)}", chain);
            Assert.Contains($"ratio={F(ratio)}", chain);
            Assert.Contains($"range={F(rangeDb)}", chain);
        }

        [Fact]
        public void AdynEq_ThresholdDb_ConvertsToLinear()
        {
            var chain = ConsonantSoftenChainBuilder.Build(new ConsonantSoftenSettings
            {
                Engine = ConsonantSoftenEngines.AdynEq,
                Preset = ConsonantSoftenPresets.Custom,
                AdynEq = new AdynEqParams { ThresholdDb = -26 },
            });

            // 10^(-26/20) ≈ 0.05012
            Assert.Contains($"threshold={F(Math.Pow(10, -26 / 20.0))}", chain);
        }

        [Fact]
        public void AdynEq_Custom_OverridesPresetParams()
        {
            var chain = ConsonantSoftenChainBuilder.Build(new ConsonantSoftenSettings
            {
                Engine = ConsonantSoftenEngines.AdynEq,
                Preset = ConsonantSoftenPresets.Custom,
                AdynEq = new AdynEqParams
                {
                    ThresholdDb = -40,
                    Ratio = 8,
                    RangeDb = 20,
                    DetectFrequencyHz = 7000,
                    TargetFrequencyHz = 7200,
                    ShelfFrequencyHz = 7000,
                    ShelfGainDb = -5,
                },
            });

            Assert.Contains("ratio=8", chain);
            Assert.Contains("range=20", chain);
            Assert.Contains("dfrequency=7000", chain);
            Assert.Contains("tfrequency=7200", chain);
            Assert.Contains("treble=f=7000:t=q:w=0.707:g=-5", chain);
        }

        // --- deesser engine ---

        [Fact]
        public void Deesser_Strong_EmitsSpecFilter()
        {
            var chain = ConsonantSoftenChainBuilder.Build(
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.Deesser, Preset = ConsonantSoftenPresets.Strong });

            Assert.Contains("deesser=i=0.7:m=0.7:f=0.5", chain);
            Assert.Contains("treble=f=6500:t=q:w=0.707:g=-3", chain);
        }

        [Theory]
        [InlineData(ConsonantSoftenPresets.Light, 0.35, 0.5)]
        [InlineData(ConsonantSoftenPresets.Medium, 0.5, 0.5)]
        [InlineData(ConsonantSoftenPresets.Strong, 0.7, 0.7)]
        public void Deesser_PresetLadder_MapsToFilterParams(string preset, double i, double m)
        {
            var chain = ConsonantSoftenChainBuilder.Build(
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.Deesser, Preset = preset });

            Assert.Contains($"deesser=i={F(i)}:m={F(m)}:f=0.5", chain);
        }

        // --- highpass (custom only) ---

        [Fact]
        public void Highpass_OmittedByDefault()
        {
            var chain = ConsonantSoftenChainBuilder.Build(
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.AdynEq, Preset = ConsonantSoftenPresets.Strong });

            Assert.DoesNotContain("highpass", chain);
        }

        [Fact]
        public void Highpass_EmittedWhenCustomSetsIt()
        {
            var chain = ConsonantSoftenChainBuilder.Build(new ConsonantSoftenSettings
            {
                Engine = ConsonantSoftenEngines.AdynEq,
                Preset = ConsonantSoftenPresets.Custom,
                AdynEq = new AdynEqParams { HighpassHz = 80 },
            });

            Assert.Contains("highpass=f=80:p=1", chain);
        }

        [Fact]
        public void Highpass_EmittedForCustomDeesser()
        {
            var chain = ConsonantSoftenChainBuilder.Build(new ConsonantSoftenSettings
            {
                Engine = ConsonantSoftenEngines.Deesser,
                Preset = ConsonantSoftenPresets.Custom,
                Deesser = new DeesserParams { HighpassHz = 120 },
            });

            Assert.Contains("highpass=f=120:p=1", chain);
        }

        // --- defaults / robustness ---

        [Fact]
        public void NullSettings_FallsBackTo_AdynEqStrong()
        {
            var chain = ConsonantSoftenChainBuilder.Build(null);

            Assert.Contains("adynamicequalizer", chain);
            Assert.Contains("ratio=6", chain);
            Assert.EndsWith("alimiter=limit=0.841:level=false", chain);
        }

        [Fact]
        public void UnknownEngine_FallsBackTo_AdynEq()
        {
            var chain = ConsonantSoftenChainBuilder.Build(
                new ConsonantSoftenSettings { Engine = "nonsense", Preset = ConsonantSoftenPresets.Strong });

            Assert.Contains("adynamicequalizer", chain);
        }

        [Fact]
        public void CustomEngine_WithoutRawParams_FallsBackToStrongDefaults()
        {
            // preset "custom" but no AdynEq params → record defaults (== Strong)
            var chain = ConsonantSoftenChainBuilder.Build(new ConsonantSoftenSettings
            {
                Engine = ConsonantSoftenEngines.AdynEq,
                Preset = ConsonantSoftenPresets.Custom,
            });

            Assert.Contains("ratio=6", chain);
            Assert.Contains("range=15", chain);
        }
    }
}
