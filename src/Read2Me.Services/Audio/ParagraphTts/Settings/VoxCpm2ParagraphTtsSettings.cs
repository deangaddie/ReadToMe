using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>
    /// Settings for ParagraphTtsServiceType.VoxCpm2 on audio.cpp. Serialized into SettingsJson; the
    /// knob names are the native server's, mapped onto audio.cpp's options by the client.
    /// </summary>
    public sealed record VoxCpm2ParagraphTtsSettings
    {
        /// <summary>audio.cpp server base URL, e.g. http://localhost:8004. Connection config, not an audio.cpp param.</summary>
        [JsonPropertyName("baseUrl")]
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

        public static VoxCpm2ParagraphTtsSettings Recommended => new()
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
            MaxChunkChars = 500,
            CarrierPrefixEnabled = false,
            CarrierMaxTargetChars = 30,
        };
    }
}
