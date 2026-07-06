using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>Settings for ParagraphTtsServiceType.ChatterboxTurbo. Serialized into SettingsJson.</summary>
    public sealed record ChatterboxTurboParagraphTtsSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8001. Connection config, not a Turbo API param.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        [JsonPropertyName("temperature")]
        public double Temperature { get; init; } = 0.8;

        [JsonPropertyName("repetition_penalty")]
        public double RepetitionPenalty { get; init; } = 1.2;

        /// <summary>Max characters per TTS chunk (soft cap). App-level chunking, not a Turbo API param.</summary>
        [JsonPropertyName("maxChunkChars")]
        public int MaxChunkChars { get; init; } = 500;

        public static ChatterboxTurboParagraphTtsSettings Recommended => new()
        {
            BaseUrl = string.Empty,
            Temperature = 0.8,
            RepetitionPenalty = 1.2,
            MaxChunkChars = 500,
        };
    }
}
