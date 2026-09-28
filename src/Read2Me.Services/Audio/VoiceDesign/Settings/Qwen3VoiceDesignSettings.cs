namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>
    /// Settings for VoiceDesignServiceType.Qwen3 on audio.cpp. Serialized into SettingsJson; the
    /// record has no JSON names, so the config forms store PascalCase keys and the schema's camelCase
    /// ones read back case-insensitively. The knob names are the native server's, mapped onto
    /// audio.cpp's by the client.
    /// </summary>
    public sealed record Qwen3VoiceDesignSettings
    {
        /// <summary>audio.cpp server base URL, e.g. http://localhost:8004.</summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>The audio.cpp <c>server.json</c> model entry the request names (task <c>vdes</c>).</summary>
        public string ModelId { get; init; } = "qwen3-design";

        /// <summary>Language code or "auto" (auto/en/zh/ja/ko/de/fr/ru/pt/es/it).</summary>
        public string Language { get; init; } = "auto";

        public double? Temperature { get; init; }

        public double? TopP { get; init; }

        public int? TopK { get; init; }

        public double? RepetitionPenalty { get; init; }

        /// <summary>audio.cpp <c>max_tokens</c>.</summary>
        public int? MaxNewTokens { get; init; }

        /// <summary>A pinned seed; null draws a random one per request so a regenerate differs.</summary>
        public int? Seed { get; init; }

        public static Qwen3VoiceDesignSettings Recommended => new();
    }
}
