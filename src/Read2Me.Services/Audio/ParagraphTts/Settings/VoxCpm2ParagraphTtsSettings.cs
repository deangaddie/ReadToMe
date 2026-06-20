namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>Settings for ParagraphTtsServiceType.VoxCpm2. Serialized into SettingsJson.</summary>
    public sealed record VoxCpm2ParagraphTtsSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8000.</summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>Max output length in tokens.</summary>
        public int MaxLen { get; init; } = 4096;
    }
}
