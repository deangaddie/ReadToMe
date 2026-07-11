using System.Globalization;
using System.Text;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Pure translation of <see cref="ConsonantSoftenSettings"/> into an ffmpeg filtergraph
    /// string. Resolves the preset ladder (custom raw params override), converts the adynEQ
    /// threshold from dB to ffmpeg's linear amplitude, and appends the mandatory hidden
    /// true-peak safety limiter that every chain must end with (EQ causes peak regrowth on
    /// loudnorm'd audio). No I/O — see <see cref="ConsonantSoftenStep"/> for the runner.
    /// </summary>
    public static class ConsonantSoftenChainBuilder
    {
        /// <summary>Hidden -1.5 dBTP true-peak ceiling appended to every chain.</summary>
        public const string LimiterTail = "alimiter=limit=0.841:level=false";

        public static string Build(ConsonantSoftenSettings? settings)
        {
            settings ??= new ConsonantSoftenSettings();

            var filters = settings.Engine == ConsonantSoftenEngines.Deesser
                ? BuildDeesser(settings)
                : BuildAdynEq(settings);

            filters.Add(LimiterTail);
            return string.Join(", ", filters);
        }

        private static List<string> BuildAdynEq(ConsonantSoftenSettings settings)
        {
            var p = settings.Preset == ConsonantSoftenPresets.Custom
                ? settings.AdynEq ?? new AdynEqParams()
                : ConsonantSoftenPresets.ResolveAdynEq(settings.Preset);

            var filters = new List<string>();
            AddHighpass(filters, p.HighpassHz);

            var threshold = Math.Pow(10, p.ThresholdDb / 20.0);
            filters.Add(
                $"adynamicequalizer=threshold={F(threshold)}" +
                $":dfrequency={F(p.DetectFrequencyHz)}:dqfactor={F(p.DetectQ)}" +
                $":tfrequency={F(p.TargetFrequencyHz)}:tqfactor={F(p.TargetQ)}" +
                $":attack={F(p.AttackMs)}:release={F(p.ReleaseMs)}" +
                $":ratio={F(p.Ratio)}:range={F(p.RangeDb)}:mode=cutabove:auto=off");
            filters.Add($"treble=f={F(p.ShelfFrequencyHz)}:t=q:w=0.707:g={F(p.ShelfGainDb)}");
            return filters;
        }

        private static List<string> BuildDeesser(ConsonantSoftenSettings settings)
        {
            var p = settings.Preset == ConsonantSoftenPresets.Custom
                ? settings.Deesser ?? new DeesserParams()
                : ConsonantSoftenPresets.ResolveDeesser(settings.Preset);

            var filters = new List<string>();
            AddHighpass(filters, p.HighpassHz);

            filters.Add($"deesser=i={F(p.Intensity)}:m={F(p.MakeupAmount)}:f={F(p.Frequency)}");
            filters.Add($"treble=f={F(p.ShelfFrequencyHz)}:t=q:w=0.707:g={F(p.ShelfGainDb)}");
            return filters;
        }

        // Optional 1-pole highpass (custom mode only; presets never set HighpassHz).
        private static void AddHighpass(List<string> filters, double? highpassHz)
        {
            if (highpassHz is { } hz && hz > 0)
                filters.Add($"highpass=f={F(hz)}:p=1");
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
