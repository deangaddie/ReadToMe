namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>Settings for VoiceDesignServiceType.Qwen3. Serialized into SettingsJson.</summary>
    public sealed record Qwen3VoiceDesignSettings
    {
        /// <summary>Server base URL, e.g. http://localhost:8100.</summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>Optional bearer token.</summary>
        public string? ApiKey { get; init; }

        /// <summary>Optional model id sent on the request.</summary>
        public string? Model { get; init; }

        /// <summary>Language code or "auto" (auto/en/zh/ja/ko/de/fr/ru/pt/es/it).</summary>
        public string Language { get; init; } = "auto";

        public double? Temperature { get; init; }

        public double? TopP { get; init; }

        public int? TopK { get; init; }

        public double? RepetitionPenalty { get; init; }

        public int? MaxNewTokens { get; init; }
    }
}
