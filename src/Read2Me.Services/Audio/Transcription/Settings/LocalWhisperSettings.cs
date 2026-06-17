namespace Read2Me.Services.Audio.Transcription.Settings
{
    /// <summary>
    /// Type-specific settings for <c>TranscriptionServiceType.LocalWhisper</c>.
    /// Serialized into <c>TranscriptionServiceConfig.SettingsJson</c>.
    /// </summary>
    public sealed record LocalWhisperSettings
    {
        /// <summary>Whisper server base URL, e.g. http://localhost:9000.</summary>
        public string BaseUrl { get; init; } = string.Empty;
    }
}
