namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Preset ladders for the consonant-soften step, per the locked spec tables. Definitions
    /// live in code so tuning fixes reach existing settings rows without a migration.
    /// Unknown preset ids fall back to Strong. "custom" is not resolved here — custom raw
    /// params are stored on <see cref="ConsonantSoftenSettings"/>; the UI seeds custom fields
    /// from whichever preset was selected at the time.
    /// </summary>
    public static class ConsonantSoftenPresets
    {
        public const string Light = "light";
        public const string Medium = "medium";
        public const string Strong = "strong";
        public const string Custom = "custom";

        public static AdynEqParams ResolveAdynEq(string preset) => preset switch
        {
            Light => new AdynEqParams { ThresholdDb = -20, Ratio = 2, RangeDb = 6 },
            Medium => new AdynEqParams { ThresholdDb = -26, Ratio = 4, RangeDb = 12 },
            _ => new AdynEqParams(),
        };

        public static DeesserParams ResolveDeesser(string preset) => preset switch
        {
            Light => new DeesserParams { Intensity = 0.35, MakeupAmount = 0.5 },
            Medium => new DeesserParams { Intensity = 0.5, MakeupAmount = 0.5 },
            _ => new DeesserParams(),
        };
    }
}
