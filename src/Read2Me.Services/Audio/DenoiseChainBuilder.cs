using System.Globalization;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Pure translation of <see cref="DenoiseSettings"/> into an ffmpeg filtergraph string.
    /// <para>
    /// <c>anlmdn</c>, never <c>afftdn</c>: at equal attenuation <c>afftdn</c> eats 1.73 dB of the
    /// 4–12 kHz air band against <c>anlmdn</c>'s 0.18 — it <i>is</i> the "watery" failure mode — and its
    /// real knob (<c>nf</c>) would force a noise-floor probe pass.
    /// </para>
    /// <para>
    /// The filter is peak-safe, so this chain carries <b>no</b> limiter tail (unlike
    /// <see cref="DePlosiveChainBuilder"/>, whose cut-only filter regrows peaks).
    /// </para>
    /// </summary>
    public static class DenoiseChainBuilder
    {
        public static string Build(DenoiseSettings? settings)
        {
            settings ??= new DenoiseSettings();

            var strength = Math.Clamp(
                settings.Strength, DenoiseSettings.MinStrength, DenoiseSettings.MaxStrength);

            return $"anlmdn=s={F(strength)}";
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
