using System.Globalization;

namespace Read2Me.Services.Audio.AudioCpp
{
    /// <summary>
    /// Qwen3-TTS's audio.cpp request conventions, shared by the Base clone and VoiceDesign:
    /// the stored language code as the language name audio.cpp's talker knows, and the optional
    /// sampling knobs under audio.cpp's option names.
    /// </summary>
    public static class Qwen3AudioCpp
    {
        // audio.cpp matches the model's codec_language_id names (lower-cased), as the native server's
        // _LANG_MAP did; "auto" lets the model pick. An unknown value passes through for audio.cpp to judge.
        private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = "auto",
            ["en"] = "english",
            ["zh"] = "chinese",
            ["de"] = "german",
            ["it"] = "italian",
            ["pt"] = "portuguese",
            ["es"] = "spanish",
            ["ja"] = "japanese",
            ["ko"] = "korean",
            ["fr"] = "french",
            ["ru"] = "russian",
        };

        /// <summary>The top-level <c>language</c> for a stored code; blank is <c>auto</c>.</summary>
        public static string Language(string? code) =>
            string.IsNullOrWhiteSpace(code) ? "auto"
            : LanguageNames.TryGetValue(code.Trim(), out var name) ? name
            : code.Trim();

        /// <summary>
        /// The sampling knobs that are set (an unset one keeps audio.cpp's default, as it kept the
        /// native server's), <c>max_new_tokens</c> as <c>max_tokens</c>, plus a random-unless-pinned seed.
        /// </summary>
        public static Dictionary<string, string> Options(
            double? temperature, double? topP, int? topK, double? repetitionPenalty, int? maxNewTokens, int? seed)
        {
            var options = new Dictionary<string, string>();
            if (temperature is { } t) options["temperature"] = Inv(t);
            if (topP is { } p) options["top_p"] = Inv(p);
            if (topK is { } k) options["top_k"] = Inv(k);
            if (repetitionPenalty is { } r) options["repetition_penalty"] = Inv(r);
            if (maxNewTokens is { } m) options["max_tokens"] = Inv(m);
            options["seed"] = AudioCppSeed.For(seed);
            return options;
        }

        private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
