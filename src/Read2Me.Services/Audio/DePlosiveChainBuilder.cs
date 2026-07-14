using System.Globalization;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Pure translation of <see cref="DePlosiveSettings"/> into an ffmpeg filtergraph string. No I/O —
    /// see <see cref="DePlosiveStep"/> for the runner.
    /// </summary>
    public static class DePlosiveChainBuilder
    {
        /// <summary>
        /// The true-peak safety limiter, mandatory on this step and not cosmetic: <c>asubcut</c> is
        /// cut-only and <i>still</i> regrows true peak to +0.000265 dB (clipped) on loudnorm'd input.
        /// Without the tail the step ships clipping.
        /// </summary>
        public const string LimiterTail = ConsonantSoftenChainBuilder.LimiterTail;

        /// <summary>Filter steepness. Fixed — the dial is the cutoff alone.</summary>
        private const int Order = 10;

        public static string Build(DePlosiveSettings? settings)
        {
            settings ??= new DePlosiveSettings();

            var cutoff = Math.Clamp(
                settings.CutoffHz, DePlosiveSettings.MinCutoffHz, DePlosiveSettings.MaxCutoffHz);

            return string.Join(", ", [$"asubcut=cutoff={F(cutoff)}:order={Order}", LimiterTail]);
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
