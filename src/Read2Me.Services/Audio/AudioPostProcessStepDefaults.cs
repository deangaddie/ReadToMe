namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The post-process chain: which steps exist, in what order, and how they behave until the
    /// user says otherwise. Step order and membership live here rather than in storage — there is
    /// no reorder UI, so "stored order" was order nobody could set, and code order means adding a
    /// step is a code change with no data touch (existing rows silently gain it). Storage supplies
    /// only enabled/settings per id, merged onto this list on read by
    /// <see cref="AudioProcessingSettingsService"/>.
    /// </summary>
    public static class AudioPostProcessStepDefaults
    {
        public static IReadOnlyList<AudioPostProcessStepConfig> All =>
        [
            // Dead air is a defect, not a taste — unlike consonant soften, this one ships on.
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.SilenceTrim, enabled: true, new SilenceTrimSettings()),
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften, enabled: false, new ConsonantSoftenSettings()),
        ];
    }
}
