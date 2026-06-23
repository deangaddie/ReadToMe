using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>Settings for VoiceDesignServiceType.VoxCpm2. Serialized into SettingsJson.</summary>
    public sealed record VoxCpm2VoiceDesignSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8003.</summary>
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

        public static VoxCpm2VoiceDesignSettings Recommended => new()
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
        };
    }
}
