namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>Settings for VoiceDesignServiceType.VoxCpm2. Serialized into SettingsJson.</summary>
    public sealed record VoxCpm2VoiceDesignSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8003.</summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>Max output length in tokens (server caps at 8192).</summary>
        public int MaxLen { get; init; } = 4096;
    }
}
