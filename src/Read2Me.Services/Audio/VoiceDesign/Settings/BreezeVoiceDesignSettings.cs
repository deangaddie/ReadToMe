using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>Settings for VoiceDesignServiceType.Breeze. Serialized into SettingsJson.</summary>
    public sealed record BreezeVoiceDesignSettings
    {
        /// <summary>audio.cpp server base URL, e.g. http://localhost:8004.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>The audio.cpp <c>server.json</c> model entry the request names (task <c>tts</c>).</summary>
        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = "breeze-design";

        /// <summary>guidance_scale — how hard the render follows the design prompt.</summary>
        [JsonPropertyName("guidanceScale")]
        public double GuidanceScale { get; init; } = 3;

        /// <summary>A pinned seed; null draws a random one per request so a regenerate differs.</summary>
        [JsonPropertyName("seed")]
        public int? Seed { get; init; }

        public static BreezeVoiceDesignSettings Recommended => new();
    }
}
