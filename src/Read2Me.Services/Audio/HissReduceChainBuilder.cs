using System.Globalization;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Pure translation of <see cref="HissReduceSettings"/> into an ffmpeg filtergraph string. No I/O —
    /// see <see cref="HissReduceStep"/> for the runner. The filter is peak-safe, so no limiter tail.
    /// </summary>
    public static class HissReduceChainBuilder
    {
        /// <summary>
        /// <c>afftdn</c>'s <c>bn</c> bands are indexed <b>relative to Nyquist</b>, not in Hz — the same
        /// sample-rate sensitivity as <c>deesser</c>'s <c>i</c>. The preset profiles aim at ~5 kHz and
        /// up <i>only because</i> the input is <see cref="CanonicalWav"/> at 24 kHz. If the canonical
        /// rate ever moves, the filter silently aims at the wrong frequencies — so it is asserted here
        /// rather than left to be discovered by ear.
        /// </summary>
        public static string Build(HissReduceSettings? settings, int sampleRateHz = CanonicalWav.SampleRateHz)
        {
            if (sampleRateHz != CanonicalWav.SampleRateHz)
                throw new ArgumentOutOfRangeException(
                    nameof(sampleRateHz), sampleRateHz,
                    $"hiss-reduce's band profile is calibrated for {CanonicalWav.SampleRateHz} Hz audio; " +
                    "afftdn's bn bands are relative to Nyquist, so another rate aims them at the wrong frequencies.");

            settings ??= new HissReduceSettings();
            var p = HissReducePresets.Resolve(settings.Preset);

            var bands = string.Join(' ', p.BandNoise.Select(F));
            return $"afftdn=nr={F(p.Nr)}:nt=custom:bn={bands}";
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
