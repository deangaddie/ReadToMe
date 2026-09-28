using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>
    /// Settings for ParagraphTtsServiceType.Qwen3Base on audio.cpp. Serialized into SettingsJson; the
    /// knob names are the native server's, mapped onto audio.cpp's by the client.
    /// </summary>
    public sealed record Qwen3ParagraphTtsSettings
    {
        /// <summary>audio.cpp server base URL, e.g. http://localhost:8004. Connection config, not an audio.cpp param.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>The audio.cpp <c>server.json</c> model entry the request names.</summary>
        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = "qwen3-base";

        /// <summary>A language code (<c>en</c>, <c>zh</c>, …) or <c>auto</c>.</summary>
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

        /// <summary>audio.cpp <c>max_tokens</c>.</summary>
        [JsonPropertyName("max_new_tokens")]
        public int? MaxNewTokens { get; init; }

        /// <summary>A pinned seed; null draws a random one per request so WER retries differ.</summary>
        [JsonPropertyName("seed")]
        public int? Seed { get; init; }

        /// <summary>Max characters per TTS chunk (soft cap). App-level chunking, not an audio.cpp param.</summary>
        [JsonPropertyName("maxChunkChars")]
        public int MaxChunkChars { get; init; } = 500;

        /// <summary>Prepend the voice's reference transcript to short text, then trim it off. App-level, not an audio.cpp param.</summary>
        [JsonPropertyName("carrierPrefixEnabled")]
        public bool CarrierPrefixEnabled { get; init; } = false;

        /// <summary>Carrier prefix applies when the target text is at most this many characters. App-level, not an audio.cpp param.</summary>
        [JsonPropertyName("carrierMaxTargetChars")]
        public int CarrierMaxTargetChars { get; init; } = 30;

        public static Qwen3ParagraphTtsSettings Recommended => new()
        {
            BaseUrl = string.Empty,
            ModelId = "qwen3-base",
            Language = "auto",
            Seed = null,
            MaxChunkChars = 500,
            CarrierPrefixEnabled = false,
            CarrierMaxTargetChars = 30,
        };
    }
}
