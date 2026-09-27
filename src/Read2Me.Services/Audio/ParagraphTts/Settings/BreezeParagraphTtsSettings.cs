using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>Settings for ParagraphTtsServiceType.Breeze. Serialized into SettingsJson.</summary>
    public sealed record BreezeParagraphTtsSettings
    {
        /// <summary>audio.cpp server base URL, e.g. http://localhost:8004.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>The audio.cpp <c>server.json</c> model entry the request names.</summary>
        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = "breeze-q8";

        /// <summary>guidance_scale for an item with VoiceInstructions — high enough to obey them.</summary>
        [JsonPropertyName("instructedGuidanceScale")]
        public double InstructedGuidanceScale { get; init; } = 3;

        /// <summary>guidance_scale for an item without VoiceInstructions — cheaper, and nothing to obey.</summary>
        [JsonPropertyName("plainGuidanceScale")]
        public double PlainGuidanceScale { get; init; } = 1;

        [JsonPropertyName("temperature")]
        public double Temperature { get; init; } = 0.9;

        [JsonPropertyName("topK")]
        public int TopK { get; init; } = 50;

        [JsonPropertyName("topP")]
        public double TopP { get; init; } = 1.0;

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

        public static BreezeParagraphTtsSettings Recommended => new();
    }
}
