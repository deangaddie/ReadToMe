namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Settings payload for the consonant-soften post-process step. A preset reference,
    /// not a snapshot: raw engine params are serialized only when <see cref="Preset"/> is
    /// <see cref="ConsonantSoftenPresets.Custom"/>, so preset tuning fixes in code propagate
    /// to existing rows for free. (Null params are omitted by
    /// <see cref="AudioPostProcessJson.Options"/>.)
    /// </summary>
    public sealed record ConsonantSoftenSettings
    {
        public string Engine { get; init; } = ConsonantSoftenEngines.AdynEq;
        public string Preset { get; init; } = ConsonantSoftenPresets.Strong;

        /// <summary>Raw adynEQ params; set only when <see cref="Preset"/> is custom.</summary>
        public AdynEqParams? AdynEq { get; init; }

        /// <summary>Raw deesser params; set only when <see cref="Preset"/> is custom.</summary>
        public DeesserParams? Deesser { get; init; }
    }

    public static class ConsonantSoftenEngines
    {
        public const string AdynEq = "adyneq";
        public const string Deesser = "deesser";
    }

    /// <summary>
    /// Full parameter set for the adynEQ engine. Threshold is held in dB (UI unit); the
    /// chain builder converts to ffmpeg's linear amplitude (10^(dB/20)). Property defaults
    /// are the Strong preset values.
    /// </summary>
    public sealed record AdynEqParams
    {
        public double ThresholdDb { get; init; } = -34;
        public double Ratio { get; init; } = 6;
        public double RangeDb { get; init; } = 15;
        public double DetectFrequencyHz { get; init; } = 6000;
        public double DetectQ { get; init; } = 0.7;
        public double TargetFrequencyHz { get; init; } = 6000;
        public double TargetQ { get; init; } = 0.7;
        public double AttackMs { get; init; } = 5;
        public double ReleaseMs { get; init; } = 60;
        public double ShelfFrequencyHz { get; init; } = 6500;
        public double ShelfGainDb { get; init; } = -3;

        /// <summary>Optional 1-pole highpass cutoff (custom mode only; presets never set it).</summary>
        public double? HighpassHz { get; init; }
    }

    /// <summary>
    /// Full parameter set for the deesser engine. Property defaults are the Strong preset values.
    /// </summary>
    public sealed record DeesserParams
    {
        public double Intensity { get; init; } = 0.7;
        public double MakeupAmount { get; init; } = 0.7;
        public double Frequency { get; init; } = 0.5;
        public double ShelfFrequencyHz { get; init; } = 6500;
        public double ShelfGainDb { get; init; } = -3;

        /// <summary>Optional 1-pole highpass cutoff (custom mode only; presets never set it).</summary>
        public double? HighpassHz { get; init; }
    }
}
