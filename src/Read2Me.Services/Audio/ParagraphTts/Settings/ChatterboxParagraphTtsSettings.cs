using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>Settings for ParagraphTtsServiceType.Chatterbox. Serialized into SettingsJson.</summary>
    public sealed record ChatterboxParagraphTtsSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8000. Connection config, not a Chatterbox API param.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        [JsonPropertyName("exaggeration")]
        public double Exaggeration { get; init; } = 0.5;

        [JsonPropertyName("cfg_weight")]
        public double CfgWeight { get; init; } = 0.5;

        [JsonPropertyName("temperature")]
        public double Temperature { get; init; } = 0.8;

        [JsonPropertyName("min_p")]
        public double MinP { get; init; } = 0.05;

        [JsonPropertyName("top_p")]
        public double TopP { get; init; } = 1.0;

        [JsonPropertyName("repetition_penalty")]
        public double RepetitionPenalty { get; init; } = 1.2;

        /// <summary>Max characters per TTS chunk (soft cap). App-level chunking, not a Chatterbox API param.</summary>
        [JsonPropertyName("maxChunkChars")]
        public int MaxChunkChars { get; init; } = 500;

        public static ChatterboxParagraphTtsSettings Recommended => new()
        {
            BaseUrl = string.Empty,
            Exaggeration = 0.5,
            CfgWeight = 0.5,
            Temperature = 0.8,
            MinP = 0.05,
            TopP = 1.0,
            RepetitionPenalty = 1.2,
            MaxChunkChars = 500,
        };
    }
}
