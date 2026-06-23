namespace Read2Me.App.Shared
{
    public enum TtsSettingsEditorMode
    {
        ProviderDefaults, // full object out; reset → VoxCpm2VoiceDesignSettings.Recommended
        VoiceOverride,    // sparse patch out; reset → configured provider defaults (Slice 3)
    }
}
