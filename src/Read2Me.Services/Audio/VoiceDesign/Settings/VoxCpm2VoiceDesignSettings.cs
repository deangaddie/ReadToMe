using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>
    /// Settings for VoiceDesignServiceType.VoxCpm2 on audio.cpp. Serialized into SettingsJson; the
    /// knob names are the native server's, mapped onto audio.cpp's options by the client.
    /// </summary>
    public sealed record VoxCpm2VoiceDesignSettings
    {
        /// <summary>audio.cpp server base URL, e.g. http://localhost:8004.</summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>The audio.cpp <c>server.json</c> model entry the request names.</summary>
        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = "voxcpm2";

        /// <summary>audio.cpp <c>guidance_scale</c>.</summary>
        [JsonPropertyName("cfg_value")]
        public double CfgValue { get; init; } = 2.0;

        /// <summary>audio.cpp <c>num_inference_steps</c>.</summary>
        [JsonPropertyName("inference_timesteps")]
        public int InferenceTimesteps { get; init; } = 10;

        /// <summary>audio.cpp <c>min_tokens</c>.</summary>
        [JsonPropertyName("min_len")]
        public int MinLen { get; init; } = 2;

        /// <summary>audio.cpp <c>max_tokens</c>.</summary>
        [JsonPropertyName("max_len")]
        public int MaxLen { get; init; } = 4096;

        [JsonPropertyName("retry_badcase")]
        public bool RetryBadcase { get; init; } = true;

        [JsonPropertyName("retry_badcase_max_times")]
        public int RetryBadcaseMaxTimes { get; init; } = 3;

        [JsonPropertyName("retry_badcase_ratio_threshold")]
        public double RetryBadcaseRatioThreshold { get; init; } = 6.0;

        /// <summary>A pinned seed; null draws a random one per request so a regenerate differs.</summary>
        [JsonPropertyName("seed")]
        public int? Seed { get; init; }

        public static VoxCpm2VoiceDesignSettings Recommended => new()
        {
            BaseUrl = string.Empty,
            ModelId = "voxcpm2",
            CfgValue = 2.0,
            InferenceTimesteps = 10,
            MinLen = 2,
            MaxLen = 4096,
            RetryBadcase = true,
            RetryBadcaseMaxTimes = 3,
            RetryBadcaseRatioThreshold = 6.0,
        };
    }
}
