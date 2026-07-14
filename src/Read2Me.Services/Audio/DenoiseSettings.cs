namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Settings payload for the denoise step (non-local means, <c>anlmdn</c>). Broadband hum/hiss
    /// removal that needs no noise-floor estimate, so the step stays one ffmpeg pass.
    /// </summary>
    /// <param name="Strength">
    /// Denoise strength. Clamped to <see cref="MinStrength"/>–<see cref="MaxStrength"/>. The filter's
    /// <c>p</c>/<c>r</c> params are CPU knobs, not quality knobs, and stay at their defaults.
    /// </param>
    public sealed record DenoiseSettings(double Strength = DenoiseSettings.DefaultStrength)
    {
        public const double MinStrength = 1;
        public const double MaxStrength = 1000;
        public const double DefaultStrength = 20;
    }
}
