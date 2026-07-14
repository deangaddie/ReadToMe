namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The post-process chain: which steps exist, in what order, and how they behave until the
    /// user says otherwise. Step order and membership live here rather than in storage — there is
    /// no reorder UI, so "stored order" was order nobody could set, and code order means adding a
    /// step is a code change with no data touch (existing rows silently gain it). Storage supplies
    /// only enabled/settings per id, merged onto this list on read by
    /// <see cref="AudioProcessingSettingsService"/>.
    /// <para>
    /// Membership <b>and defaults</b> are per <see cref="StepScope"/>: the same step id carries
    /// different defaults in each scope, because the two kinds of audio fail differently. Paragraph
    /// audio is synthetic, so it has no capture artefacts and wants the aggressive settings; voice
    /// reference audio may be a real mic recording, so every voice-side default is the gentlest one
    /// on its ladder — the user re-runs the editor to go harder.
    /// </para>
    /// </summary>
    public static class AudioPostProcessStepDefaults
    {
        /// <summary>
        /// The steps offered in <paramref name="scope"/>, in chain order.
        /// <para>
        /// Voice order is forced by real interactions between the steps, not taste: denoise must run
        /// before silence-trim or the threshold detector reads the noise floor as signal and trims
        /// nothing. The voice editor is therefore a checklist, not a chain builder.
        /// </para>
        /// <para>
        /// <see cref="AudioPostProcessStepConfig.Enabled"/> is meaningless in <see cref="StepScope.Voice"/>
        /// — the editor's checkboxes all start off and the user's ticks are the chain. The field stays
        /// because it is the config's shape; Voice callers ignore it.
        /// </para>
        /// </summary>
        public static IReadOnlyList<AudioPostProcessStepConfig> For(StepScope scope) => scope switch
        {
            StepScope.Voice => Voice,
            _ => Paragraph,
        };

        private static IReadOnlyList<AudioPostProcessStepConfig> Paragraph =>
        [
            // Dead air is a defect, not a taste — unlike consonant soften, this one ships on.
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.SilenceTrim, enabled: true, new SilenceTrimSettings()),
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, enabled: false, new ConsonantSoftenSettings()),
        ];

        private static IReadOnlyList<AudioPostProcessStepConfig> Voice =>
        [
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.DePlosive, enabled: false, new DePlosiveSettings()),
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.Denoise, enabled: false, new DenoiseSettings()),
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.HissReduce, enabled: false, new HissReduceSettings()),
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, enabled: false,
                new ConsonantSoftenSettings { Preset = ConsonantSoftenPresets.Light }),
            // -35 dB, not the paragraph -50: detection=peak compares against the noise floor's peak,
            // so -50 removes exactly 0 ms from a mic clip. -30 is the line where speech starts going,
            // so the voice-side dial caps at -35. The guard rises with it — a reference voice trimmed
            // below a second has gone wrong.
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.SilenceTrim, enabled: false,
                new SilenceTrimSettings(ThresholdDb: -35, PadMs: 50, MinOutputMs: 1000)),
        ];
    }
}
