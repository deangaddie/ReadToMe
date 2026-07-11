using Read2Me.Services.Audio;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// Edit-state for the consonant-soften post-process step config. Presets are stored by
    /// reference, so raw params are only written when <see cref="Preset"/> is custom. The
    /// preset picker doubles as the reset: selecting any preset (including flipping back to
    /// custom) re-seeds the drafts from that preset's resolved params, discarding unsaved
    /// tweaks. Saved custom params survive a reload.
    /// </summary>
    public sealed class ConsonantSoftenForm
    {
        public bool Enabled { get; set; }
        public string Engine { get; private set; } = ConsonantSoftenEngines.AdynEq;
        public string Preset { get; private set; } = ConsonantSoftenPresets.Strong;

        /// <summary>Editable adynEQ params, live only in custom mode.</summary>
        public AdynEqDraft AdynEq { get; private set; } = new();

        /// <summary>Editable deesser params, live only in custom mode.</summary>
        public DeesserDraft Deesser { get; private set; } = new();

        /// <summary>Optional highpass, shared by both engines. Presets never set it.</summary>
        public bool HighpassEnabled { get; set; }
        public double HighpassHz { get; set; } = DefaultHighpassHz;

        public const double DefaultHighpassHz = 80;

        public bool IsCustom => Preset == ConsonantSoftenPresets.Custom;

        /// <summary>The preset the custom drafts are seeded from when custom is (re)selected.</summary>
        private string _seedPreset = ConsonantSoftenPresets.Strong;

        public static ConsonantSoftenForm FromConfig(AudioPostProcessStepConfig? config)
        {
            var settings = config?.GetSettings<ConsonantSoftenSettings>() ?? new ConsonantSoftenSettings();
            var form = new ConsonantSoftenForm
            {
                Enabled = config?.Enabled ?? false,
                Engine = settings.Engine,
                Preset = settings.Preset,
            };

            var seed = settings.Preset == ConsonantSoftenPresets.Custom
                ? ConsonantSoftenPresets.Strong
                : settings.Preset;
            form._seedPreset = seed;
            form.SeedDrafts(seed);

            // Saved custom params win over the seed — they are the user's last save.
            if (settings.AdynEq is { } adynEq) form.AdynEq = AdynEqDraft.From(adynEq);
            if (settings.Deesser is { } deesser) form.Deesser = DeesserDraft.From(deesser);

            var highpass = settings.AdynEq?.HighpassHz ?? settings.Deesser?.HighpassHz;
            form.HighpassEnabled = highpass.HasValue;
            form.HighpassHz = highpass ?? DefaultHighpassHz;

            return form;
        }

        public void SetEngine(string engine) => Engine = engine;

        /// <summary>
        /// Selects a preset. Non-custom presets become the seed for later custom edits;
        /// either way the drafts are re-seeded, so unsaved tweaks are discarded.
        /// </summary>
        public void SetPreset(string preset)
        {
            Preset = preset;
            if (preset != ConsonantSoftenPresets.Custom) _seedPreset = preset;
            SeedDrafts(_seedPreset);
        }

        public AudioPostProcessStepConfig BuildConfig()
        {
            var custom = IsCustom;
            var hp = HighpassEnabled ? HighpassHz : (double?)null;
            var settings = new ConsonantSoftenSettings
            {
                Engine = Engine,
                Preset = Preset,
                AdynEq = custom ? AdynEq.ToParams(hp) : null,
                Deesser = custom ? Deesser.ToParams(hp) : null,
            };
            return AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, Enabled, settings);
        }

        private void SeedDrafts(string preset)
        {
            AdynEq = AdynEqDraft.From(ConsonantSoftenPresets.ResolveAdynEq(preset));
            Deesser = DeesserDraft.From(ConsonantSoftenPresets.ResolveDeesser(preset));
            // Presets never carry a highpass, so the reset clears it too.
            HighpassEnabled = false;
            HighpassHz = DefaultHighpassHz;
        }
    }

    /// <summary>Mutable mirror of <see cref="AdynEqParams"/> for two-way form binding.</summary>
    public sealed class AdynEqDraft
    {
        public double ThresholdDb { get; set; }
        public double Ratio { get; set; }
        public double RangeDb { get; set; }
        public double DetectFrequencyHz { get; set; }
        public double DetectQ { get; set; }
        public double TargetFrequencyHz { get; set; }
        public double TargetQ { get; set; }
        public double AttackMs { get; set; }
        public double ReleaseMs { get; set; }
        public double ShelfFrequencyHz { get; set; }
        public double ShelfGainDb { get; set; }

        public static AdynEqDraft From(AdynEqParams p) => new()
        {
            ThresholdDb = p.ThresholdDb,
            Ratio = p.Ratio,
            RangeDb = p.RangeDb,
            DetectFrequencyHz = p.DetectFrequencyHz,
            DetectQ = p.DetectQ,
            TargetFrequencyHz = p.TargetFrequencyHz,
            TargetQ = p.TargetQ,
            AttackMs = p.AttackMs,
            ReleaseMs = p.ReleaseMs,
            ShelfFrequencyHz = p.ShelfFrequencyHz,
            ShelfGainDb = p.ShelfGainDb,
        };

        public AdynEqParams ToParams(double? highpassHz) => new()
        {
            ThresholdDb = ThresholdDb,
            Ratio = Ratio,
            RangeDb = RangeDb,
            DetectFrequencyHz = DetectFrequencyHz,
            DetectQ = DetectQ,
            TargetFrequencyHz = TargetFrequencyHz,
            TargetQ = TargetQ,
            AttackMs = AttackMs,
            ReleaseMs = ReleaseMs,
            ShelfFrequencyHz = ShelfFrequencyHz,
            ShelfGainDb = ShelfGainDb,
            HighpassHz = highpassHz,
        };
    }

    /// <summary>Mutable mirror of <see cref="DeesserParams"/> for two-way form binding.</summary>
    public sealed class DeesserDraft
    {
        public double Intensity { get; set; }
        public double MakeupAmount { get; set; }
        public double Frequency { get; set; }
        public double ShelfFrequencyHz { get; set; }
        public double ShelfGainDb { get; set; }

        public static DeesserDraft From(DeesserParams p) => new()
        {
            Intensity = p.Intensity,
            MakeupAmount = p.MakeupAmount,
            Frequency = p.Frequency,
            ShelfFrequencyHz = p.ShelfFrequencyHz,
            ShelfGainDb = p.ShelfGainDb,
        };

        public DeesserParams ToParams(double? highpassHz) => new()
        {
            Intensity = Intensity,
            MakeupAmount = MakeupAmount,
            Frequency = Frequency,
            ShelfFrequencyHz = ShelfFrequencyHz,
            ShelfGainDb = ShelfGainDb,
            HighpassHz = highpassHz,
        };
    }
}
