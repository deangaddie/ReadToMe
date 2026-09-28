using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>
    /// Settings for ParagraphTtsServiceType.Chatterbox on audio.cpp. Serialized into SettingsJson; the
    /// knob names are the native server's, mapped onto audio.cpp's options by the client.
    /// </summary>
    public sealed record ChatterboxParagraphTtsSettings
    {
        /// <summary>audio.cpp server base URL, e.g. http://localhost:8004. Connection config, not an audio.cpp param.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>The audio.cpp <c>server.json</c> model entry the request names.</summary>
        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = "chatterbox";

        [JsonPropertyName("exaggeration")]
        public double Exaggeration { get; init; } = 0.5;

        /// <summary>audio.cpp <c>guidance_scale</c>.</summary>
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

        public static ChatterboxParagraphTtsSettings Recommended => new()
        {
            BaseUrl = string.Empty,
            ModelId = "chatterbox",
            Exaggeration = 0.5,
            CfgWeight = 0.5,
            Temperature = 0.8,
            MinP = 0.05,
            TopP = 1.0,
            RepetitionPenalty = 1.2,
            Seed = null,
            MaxChunkChars = 500,
            CarrierPrefixEnabled = false,
            CarrierMaxTargetChars = 30,
        };
    }
}
