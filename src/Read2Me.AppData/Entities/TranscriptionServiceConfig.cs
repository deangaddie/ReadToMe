namespace Read2Me.AppData.Entities
{
    /// <summary>
    /// A configured transcription (speech-to-text) backend. Common identity lives
    /// in columns; all backend-specific settings are serialized into
    /// <see cref="SettingsJson"/>, keyed by <see cref="Type"/>. This keeps new
    /// backend types additive — no schema change per type.
    /// </summary>
    public class TranscriptionServiceConfig
    {
        public int Id { get; set; }

        /// <summary>User-facing name for this configuration.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Selects the backend implementation and the settings shape.</summary>
        public TranscriptionServiceType Type { get; set; }

        /// <summary>Serialized type-specific settings (e.g. base URL, API token).</summary>
        public string SettingsJson { get; set; } = string.Empty;
    }
}
