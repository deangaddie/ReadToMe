using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    /// <summary>Settings for ParagraphTtsServiceType.VoxCpm2. Serialized into SettingsJson.</summary>
    public sealed record VoxCpm2ParagraphTtsSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8000. Connection config, not a VoxCPM2 API param.</summary>
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; init; } = string.Empty;

        [JsonPropertyName("cfg_value")]
        public double CfgValue { get; init; } = 2.0;

        [JsonPropertyName("inference_timesteps")]
        public int InferenceTimesteps { get; init; } = 10;

        [JsonPropertyName("min_len")]
        public int MinLen { get; init; } = 2;

        [JsonPropertyName("max_len")]
        public int MaxLen { get; init; } = 4096;

        [JsonPropertyName("normalize")]
        public bool Normalize { get; init; } = false;

        [JsonPropertyName("denoise")]
        public bool Denoise { get; init; } = false;

        [JsonPropertyName("retry_badcase")]
        public bool RetryBadcase { get; init; } = true;

        [JsonPropertyName("retry_badcase_max_times")]
        public int RetryBadcaseMaxTimes { get; init; } = 3;

        [JsonPropertyName("retry_badcase_ratio_threshold")]
        public double RetryBadcaseRatioThreshold { get; init; } = 6.0;

        /// <summary>Max characters per TTS chunk (soft cap). App-level chunking, not a VoxCPM2 API param.</summary>
        [JsonPropertyName("maxChunkChars")]
        public int MaxChunkChars { get; init; } = 500;

        public static VoxCpm2ParagraphTtsSettings Recommended => new()
        {
            BaseUrl = string.Empty,
            CfgValue = 2.0,
            InferenceTimesteps = 10,
            MinLen = 2,
            MaxLen = 4096,
            Normalize = false,
            Denoise = false,
            RetryBadcase = true,
            RetryBadcaseMaxTimes = 3,
            RetryBadcaseRatioThreshold = 6.0,
            MaxChunkChars = 500,
        };
    }
}
