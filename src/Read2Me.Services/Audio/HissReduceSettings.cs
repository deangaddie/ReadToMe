namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Settings payload for the hiss-reduce step. A preset reference rather than raw params: the
    /// <c>bn</c> band profile is a 15-number vector nobody can tune by hand, and its bands are indexed
    /// relative to Nyquist — see <see cref="HissReduceChainBuilder"/>.
    /// </summary>
    public sealed record HissReduceSettings
    {
        public string Preset { get; init; } = HissReducePresets.Light;
    }

    /// <summary>
    /// The hiss-reduce ladder. Both presets share the same HF-weighted shape: strongly negative in the
    /// speech bands (so the filter leaves them alone) and positive above ~5 kHz, where hiss lives.
    /// Unknown preset ids fall back to Light — the gentle end, as everywhere in Voice scope.
    /// </summary>
    public static class HissReducePresets
    {
        public const string Light = "light";
        public const string Strong = "strong";

        public static HissReduceParams Resolve(string preset) => preset switch
        {
            Strong => new HissReduceParams(
                Nr: 30, BandNoise: [-40, -40, -40, -40, -40, -40, -30, -20, -5, 5, 15, 25, 30, 30, 30]),
            _ => new HissReduceParams(
                Nr: 12, BandNoise: [-20, -20, -20, -20, -20, -20, -20, -10, 0, 5, 10, 15, 20, 20, 20]),
        };
    }

    /// <summary>Resolved <c>afftdn</c> params for one hiss-reduce preset.</summary>
    /// <param name="Nr">Noise reduction in dB.</param>
    /// <param name="BandNoise">The 15-band custom noise profile (<c>bn</c>).</param>
    public sealed record HissReduceParams(double Nr, IReadOnlyList<double> BandNoise);
}
