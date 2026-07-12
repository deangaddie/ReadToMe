using Read2Me.Services.Audio;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// Edit-state for the silence-trim post-process step config. Raw params, no preset ladder —
    /// both fields mean exactly what they say, so there is nothing to resolve.
    /// </summary>
    public sealed class SilenceTrimForm
    {
        public bool Enabled { get; set; } = true;
        public double ThresholdDb { get; set; } = Defaults.ThresholdDb;
        public int PadMs { get; set; } = Defaults.PadMs;

        private static readonly SilenceTrimSettings Defaults = new();

        public static SilenceTrimForm FromConfig(AudioPostProcessStepConfig? config)
        {
            var settings = config?.GetSettings<SilenceTrimSettings>() ?? new SilenceTrimSettings();
            return new SilenceTrimForm
            {
                Enabled = config?.Enabled ?? true,
                ThresholdDb = settings.ThresholdDb,
                PadMs = settings.PadMs,
            };
        }

        public AudioPostProcessStepConfig BuildConfig() =>
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.SilenceTrim, Enabled,
                new SilenceTrimSettings(ThresholdDb, Math.Max(0, PadMs)));
    }
}
