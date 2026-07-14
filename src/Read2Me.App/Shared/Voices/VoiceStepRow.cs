using System;
using Read2Me.Services.Audio;

namespace Read2Me.App.Shared.Voices
{
    /// <summary>
    /// One row of the voice editor's checklist: the tick, the step's dials as mutable drafts, and the
    /// outcome of the last render. Seeded from the Voice-scope defaults, which are the gentlest setting
    /// on every ladder — the user re-runs the editor to go harder.
    /// </summary>
    public sealed class VoiceStepRow
    {
        private VoiceStepRow(string stepId, string label, string blurb)
        {
            StepId = stepId;
            Label = label;
            Blurb = blurb;
        }

        public string StepId { get; }
        public string Label { get; }
        public string Blurb { get; }

        public bool Ticked { get; internal set; }

        // ── Dials. Each step reads only its own; the razor shows only the selected row's. ──

        public double CutoffHz { get; set; } = DePlosiveSettings.DefaultCutoffHz;
        public double Strength { get; set; } = DenoiseSettings.DefaultStrength;
        public string Preset { get; set; } = "";
        public double ThresholdDb { get; set; }
        public int PadMs { get; set; }
        private double _minOutputMs;

        // ── Last render's outcome ────────────────────────────────────────────────

        public bool HasPreview { get; private set; }

        /// <summary>False when the step fell back to its input — its player is identical to the one above.</summary>
        public bool Applied { get; private set; }
        public string? SkipReason { get; private set; }

        /// <summary>Stable across renders: the store's tokens are minted per page, not per render.</summary>
        public string Token(string pageId) => $"{pageId}-{StepId}";

        public static VoiceStepRow From(AudioPostProcessStepConfig defaults)
        {
            var row = new VoiceStepRow(defaults.StepId, LabelFor(defaults.StepId), BlurbFor(defaults.StepId));

            switch (defaults.StepId)
            {
                case AudioPostProcessStepIds.DePlosive:
                    row.CutoffHz = (defaults.GetSettings<DePlosiveSettings>() ?? new DePlosiveSettings()).CutoffHz;
                    break;
                case AudioPostProcessStepIds.Denoise:
                    row.Strength = (defaults.GetSettings<DenoiseSettings>() ?? new DenoiseSettings()).Strength;
                    break;
                case AudioPostProcessStepIds.HissReduce:
                    row.Preset = (defaults.GetSettings<HissReduceSettings>() ?? new HissReduceSettings()).Preset;
                    break;
                case AudioPostProcessStepIds.ConsonantSoften:
                    row.Preset = (defaults.GetSettings<ConsonantSoftenSettings>() ?? new ConsonantSoftenSettings()).Preset;
                    break;
                case AudioPostProcessStepIds.SilenceTrim:
                    var trim = defaults.GetSettings<SilenceTrimSettings>() ?? new SilenceTrimSettings();
                    row.ThresholdDb = trim.ThresholdDb;
                    row.PadMs = trim.PadMs;
                    row._minOutputMs = trim.MinOutputMs;
                    break;
            }

            return row;
        }

        /// <summary>
        /// The config this row's dials describe. <c>Enabled</c> is meaningless in Voice scope — the tick
        /// is what puts the step in the chain — so it is always true here: a step in the chain runs.
        /// </summary>
        public AudioPostProcessStepConfig BuildConfig() => StepId switch
        {
            AudioPostProcessStepIds.DePlosive =>
                AudioPostProcessStepConfig.Create(StepId, true, new DePlosiveSettings(CutoffHz)),
            AudioPostProcessStepIds.Denoise =>
                AudioPostProcessStepConfig.Create(StepId, true, new DenoiseSettings(Strength)),
            AudioPostProcessStepIds.HissReduce =>
                AudioPostProcessStepConfig.Create(StepId, true, new HissReduceSettings { Preset = Preset }),
            AudioPostProcessStepIds.ConsonantSoften =>
                AudioPostProcessStepConfig.Create(StepId, true, new ConsonantSoftenSettings { Preset = Preset }),
            AudioPostProcessStepIds.SilenceTrim =>
                AudioPostProcessStepConfig.Create(
                    StepId, true, new SilenceTrimSettings(ThresholdDb, PadMs, _minOutputMs)),
            _ => throw new InvalidOperationException($"No settings shape for step '{StepId}'"),
        };

        internal void RecordOutcome(ChainStepOutcome outcome)
        {
            HasPreview = true;
            Applied = outcome.Applied;
            SkipReason = outcome.Applied ? null : outcome.Reason;
        }

        internal void Reset()
        {
            Ticked = false;
            HasPreview = false;
            Applied = false;
            SkipReason = null;
        }

        private static string LabelFor(string stepId) => stepId switch
        {
            AudioPostProcessStepIds.DePlosive => "De-plosive",
            AudioPostProcessStepIds.Denoise => "Denoise",
            AudioPostProcessStepIds.HissReduce => "Hiss reduce",
            AudioPostProcessStepIds.ConsonantSoften => "Consonant soften",
            AudioPostProcessStepIds.SilenceTrim => "Silence trim",
            _ => stepId,
        };

        private static string BlurbFor(string stepId) => stepId switch
        {
            AudioPostProcessStepIds.DePlosive => "Cuts the subsonic thump a 'p' or 'b' puts into the mic.",
            AudioPostProcessStepIds.Denoise => "Removes broadband room noise and hum.",
            AudioPostProcessStepIds.HissReduce => "Attenuates hiss above 5 kHz only, leaving speech untouched.",
            AudioPostProcessStepIds.ConsonantSoften => "Tames harsh sibilants.",
            AudioPostProcessStepIds.SilenceTrim => "Trims dead air from the start and end.",
            _ => "",
        };
    }
}
