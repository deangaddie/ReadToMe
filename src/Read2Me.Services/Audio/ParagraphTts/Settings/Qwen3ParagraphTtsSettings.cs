using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>Settings for ParagraphTtsServiceType.Qwen3Base. Serialized into SettingsJson.</summary>
    public sealed record Qwen3ParagraphTtsSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8101.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>Optional bearer token.</summary>
        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; init; }

        [JsonPropertyName("language")]
        public string Language { get; init; } = "auto";

        [JsonPropertyName("temperature")]
        public double? Temperature { get; init; }

        [JsonPropertyName("top_p")]
        public double? TopP { get; init; }

        [JsonPropertyName("top_k")]
        public int? TopK { get; init; }

        [JsonPropertyName("repetition_penalty")]
        public double? RepetitionPenalty { get; init; }

        [JsonPropertyName("max_new_tokens")]
        public int? MaxNewTokens { get; init; }

        /// <summary>Max characters per TTS chunk (soft cap). App-level chunking, not a Qwen3 API param.</summary>
        [JsonPropertyName("maxChunkChars")]
        public int MaxChunkChars { get; init; } = 500;

        /// <summary>Prepend the voice's reference transcript to short text, then trim it off. App-level, not a Qwen3 API param.</summary>
        [JsonPropertyName("carrierPrefixEnabled")]
        public bool CarrierPrefixEnabled { get; init; } = false;

        /// <summary>Carrier prefix applies when the target text is at most this many characters. App-level, not a Qwen3 API param.</summary>
        [JsonPropertyName("carrierMaxTargetChars")]
        public int CarrierMaxTargetChars { get; init; } = 30;

        public static Qwen3ParagraphTtsSettings Recommended => new()
        {
            BaseUrl = string.Empty,
            Language = "auto",
            MaxChunkChars = 500,
            CarrierPrefixEnabled = false,
            CarrierMaxTargetChars = 30,
        };
    }
}
