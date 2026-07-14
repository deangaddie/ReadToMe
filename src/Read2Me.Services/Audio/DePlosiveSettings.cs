namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Settings payload for the de-plosive step: a subsonic cut below the voice's fundamental, which
    /// is where a mic's plosive thump lives. One dial — <c>order</c> is fixed at 10 and never exposed.
    /// </summary>
    /// <param name="CutoffHz">
    /// Cut frequency. Clamped to <see cref="MinCutoffHz"/>–<see cref="MaxCutoffHz"/>: above ~100 Hz the
    /// filter starts eating the male fundamental, which is what the cap is for.
    /// </param>
    public sealed record DePlosiveSettings(double CutoffHz = DePlosiveSettings.DefaultCutoffHz)
    {
        public const double MinCutoffHz = 40;
        public const double MaxCutoffHz = 120;
        public const double DefaultCutoffHz = 60;
    }
}
