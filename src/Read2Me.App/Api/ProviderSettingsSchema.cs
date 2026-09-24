using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.App.Api
{
    /// <summary>One choice of an <c>enum</c> field.</summary>
    public sealed record SettingsOptionDto(string Value, string? Label = null);

    /// <summary>
    /// One editable field of a provider's settings record. <see cref="Key"/> is the JSON property
    /// name the provider's <c>*Settings</c> record serialises to, so a sparse patch of these keys
    /// merges straight over the config's <c>settingsJson</c> (<see cref="VoiceDesignSettingsMerge"/>).
    /// A number with both <see cref="Min"/> and <see cref="Max"/> renders as a slider. A
    /// <see cref="Nullable"/> number may be left blank, meaning "use the server's default".
    /// </summary>
    public sealed record SettingsFieldDto(
        string Key,
        string Label,
        string Kind,
        double? Min = null,
        double? Max = null,
        double? Step = null,
        IReadOnlyList<SettingsOptionDto>? Options = null,
        object? Default = null,
        string? Help = null,
        bool Nullable = false);

    /// <summary>The fields a provider type exposes, with the recommended default per field.</summary>
    public sealed record SettingsSchemaDto(string Type, IReadOnlyList<SettingsFieldDto> Fields);

    /// <summary>
    /// One static descriptor per TTS / voice-design provider (Angular ticket 16): the fields the
    /// Blazor typed editors (<c>ParagraphTtsSettingsEditor</c>, <c>TtsSettingsEditor</c>) render,
    /// with the same ranges and the <c>Recommended</c> record's defaults. Connection settings
    /// (<c>baseUrl</c>, <c>apiKey</c>) and the app-level chunking / carrier knobs are not here —
    /// they are per-config, never per-voice, exactly as the <c>*SettingsDiff</c> classes skip them.
    /// </summary>
    public static class ProviderSettingsSchema
    {
        private static readonly IReadOnlyList<SettingsOptionDto> Qwen3Languages =
            new[] { "auto", "en", "zh", "ja", "ko", "de", "fr", "ru", "pt", "es", "it" }
                .Select(l => new SettingsOptionDto(l)).ToList();

        public static SettingsSchemaDto ParagraphTts(ParagraphTtsServiceType type) => type switch
        {
            ParagraphTtsServiceType.VoxCpm2 => new(type.ToString(), VoxCpm2Fields(
                VoxCpm2ParagraphTtsSettings.Recommended.CfgValue,
                VoxCpm2ParagraphTtsSettings.Recommended.InferenceTimesteps,
                VoxCpm2ParagraphTtsSettings.Recommended.MinLen,
                VoxCpm2ParagraphTtsSettings.Recommended.MaxLen,
                VoxCpm2ParagraphTtsSettings.Recommended.Normalize,
                VoxCpm2ParagraphTtsSettings.Recommended.Denoise,
                VoxCpm2ParagraphTtsSettings.Recommended.RetryBadcase,
                VoxCpm2ParagraphTtsSettings.Recommended.RetryBadcaseMaxTimes,
                VoxCpm2ParagraphTtsSettings.Recommended.RetryBadcaseRatioThreshold)),
            ParagraphTtsServiceType.Chatterbox => new(type.ToString(), ChatterboxFields()),
            ParagraphTtsServiceType.ChatterboxTurbo => new(type.ToString(), ChatterboxTurboFields()),
            ParagraphTtsServiceType.Qwen3Base => new(type.ToString(), Qwen3BaseFields()),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No settings schema for this TTS provider."),
        };

        public static SettingsSchemaDto VoiceDesign(VoiceDesignServiceType type) => type switch
        {
            VoiceDesignServiceType.VoxCpm2 => new(type.ToString(), VoxCpm2Fields(
                VoxCpm2VoiceDesignSettings.Recommended.CfgValue,
                VoxCpm2VoiceDesignSettings.Recommended.InferenceTimesteps,
                VoxCpm2VoiceDesignSettings.Recommended.MinLen,
                VoxCpm2VoiceDesignSettings.Recommended.MaxLen,
                VoxCpm2VoiceDesignSettings.Recommended.Normalize,
                VoxCpm2VoiceDesignSettings.Recommended.Denoise,
                VoxCpm2VoiceDesignSettings.Recommended.RetryBadcase,
                VoxCpm2VoiceDesignSettings.Recommended.RetryBadcaseMaxTimes,
                VoxCpm2VoiceDesignSettings.Recommended.RetryBadcaseRatioThreshold)),
            VoiceDesignServiceType.Qwen3 => new(type.ToString(), Qwen3VoiceDesignFields()),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No settings schema for this voice-design provider."),
        };

        /// <summary>A Whisper server is its base URL and nothing else.</summary>
        public static SettingsSchemaDto Transcription(TranscriptionServiceType type) => new(type.ToString(), []);

        /// <summary>Both similarity models share one record; the key is its (unnamed, so PascalCase) JSON property.</summary>
        public static SettingsSchemaDto SemanticSimilarity(SemanticSimilarityServiceType type) => new(type.ToString(),
        [
            new(nameof(SemanticSimilaritySettings.PassThreshold), "Pass Threshold", "number", Min: 0.0, Max: 1.0, Step: 0.01,
                Default: new SemanticSimilaritySettings().PassThreshold,
                Help: "Similarity at or above this passes the semantic accuracy check. (0–1, exclusive)"),
        ]);

        /// <summary>Shared by the VoxCPM2 TTS and voice-design records: same JSON names, same ranges.</summary>
        private static IReadOnlyList<SettingsFieldDto> VoxCpm2Fields(
            double cfgValue, int inferenceTimesteps, int minLen, int maxLen, bool normalize, bool denoise,
            bool retryBadcase, int retryBadcaseMaxTimes, double retryBadcaseRatioThreshold) =>
        [
            new("cfg_value", "CFG Value", "number", Min: 1.0, Max: 5.0, Step: 0.1, Default: cfgValue,
                Help: "Guidance scale — higher = closer to design prompt. (1.0–5.0)"),
            new("inference_timesteps", "LocDiT Steps", "number", Min: 1, Max: 50, Step: 1, Default: inferenceTimesteps,
                Help: "Flow-matching diffusion steps — speed vs quality. (1–50)"),
            new("min_len", "Min Length (s)", "number", Min: 1, Max: 100, Step: 1, Default: minLen,
                Help: "Minimum output length in seconds."),
            new("max_len", "Max Length (tokens)", "number", Min: 10, Max: 8192, Step: 1, Default: maxLen,
                Help: "Maximum output length in tokens (server caps at 8192)."),
            new("normalize", "Text Normalization", "boolean", Default: normalize,
                Help: "Expand numbers, dates, abbreviations."),
            new("denoise", "Denoise Reference Audio", "boolean", Default: denoise,
                Help: "Clean the reference audio (ZipEnhancer)."),
            new("retry_badcase", "Retry Bad Cases", "boolean", Default: retryBadcase,
                Help: "Re-synthesize when a bad result is detected."),
            new("retry_badcase_max_times", "Max Retries", "number", Min: 1, Max: 10, Step: 1, Default: retryBadcaseMaxTimes,
                Help: "Maximum retry attempts."),
            new("retry_badcase_ratio_threshold", "Ratio Threshold", "number", Min: 1.0, Step: 0.1, Default: retryBadcaseRatioThreshold,
                Help: "Bad-case detection ratio threshold."),
        ];

        private static IReadOnlyList<SettingsFieldDto> ChatterboxFields()
        {
            var r = ChatterboxParagraphTtsSettings.Recommended;
            return
            [
                new("exaggeration", "Exaggeration", "number", Min: 0.0, Max: 2.0, Step: 0.05, Default: r.Exaggeration,
                    Help: "Emotional intensity. (0.0–2.0)"),
                new("cfg_weight", "CFG Weight", "number", Min: 0.0, Max: 1.0, Step: 0.05, Default: r.CfgWeight,
                    Help: "Pace/guidance weight. (0.0–1.0)"),
                new("temperature", "Temperature", "number", Min: 0.0, Max: 2.0, Step: 0.05, Default: r.Temperature,
                    Help: "Sampling randomness. (0.0–2.0)"),
                new("min_p", "Min P", "number", Min: 0.0, Max: 1.0, Step: 0.01, Default: r.MinP,
                    Help: "Nucleus sampling floor."),
                new("top_p", "Top P", "number", Min: 0.0, Max: 1.0, Step: 0.01, Default: r.TopP,
                    Help: "Nucleus sampling ceiling."),
                new("repetition_penalty", "Repetition Penalty", "number", Min: 1.0, Max: 2.0, Step: 0.05, Default: r.RepetitionPenalty,
                    Help: "Penalizes repeated tokens."),
            ];
        }

        private static IReadOnlyList<SettingsFieldDto> ChatterboxTurboFields()
        {
            var r = ChatterboxTurboParagraphTtsSettings.Recommended;
            return
            [
                new("temperature", "Temperature", "number", Min: 0.0, Max: 2.0, Step: 0.05, Default: r.Temperature,
                    Help: "Sampling randomness. (0.0–2.0)"),
                new("repetition_penalty", "Repetition Penalty", "number", Min: 1.0, Max: 2.0, Step: 0.05, Default: r.RepetitionPenalty,
                    Help: "Penalizes repeated tokens. Expression comes from inline paralinguistic tags in the text: [laugh] [chuckle] [sigh] [cough] [clear throat] [gasp] [groan] [sniff] [shush]"),
            ];
        }

        /// <summary>Qwen3-Base TTS: snake_case JSON names, sampling knobs nullable ("server default").</summary>
        private static IReadOnlyList<SettingsFieldDto> Qwen3BaseFields()
        {
            var r = Qwen3ParagraphTtsSettings.Recommended;
            return
            [
                new("language", "Language", "enum", Options: Qwen3Languages, Default: r.Language),
                ..Qwen3SamplingFields("temperature", "top_p", "top_k", "repetition_penalty", "max_new_tokens"),
            ];
        }

        /// <summary>Qwen3 voice design: the record has no JSON names, so the web-default camelCase keys apply.</summary>
        private static IReadOnlyList<SettingsFieldDto> Qwen3VoiceDesignFields()
        {
            var r = new Qwen3VoiceDesignSettings();
            return
            [
                new("language", "Language", "enum", Options: Qwen3Languages, Default: r.Language),
                ..Qwen3SamplingFields("temperature", "topP", "topK", "repetitionPenalty", "maxNewTokens"),
            ];
        }

        private static IEnumerable<SettingsFieldDto> Qwen3SamplingFields(
            string temperature, string topP, string topK, string repetitionPenalty, string maxNewTokens) =>
        [
            new(temperature, "Temperature", "number", Min: 0.0, Max: 2.0, Step: 0.05, Nullable: true,
                Help: "Sampling randomness. Leave blank to use server default."),
            new(topP, "Top P", "number", Min: 0.0, Max: 1.0, Step: 0.01, Nullable: true,
                Help: "Nucleus sampling ceiling. Leave blank to use server default."),
            new(topK, "Top K", "number", Min: 1, Step: 1, Nullable: true,
                Help: "Top-K sampling cutoff. Leave blank to use server default."),
            new(repetitionPenalty, "Repetition Penalty", "number", Min: 1.0, Max: 2.0, Step: 0.05, Nullable: true,
                Help: "Penalizes repeated tokens. Leave blank to use server default."),
            new(maxNewTokens, "Max New Tokens", "number", Min: 1, Step: 1, Nullable: true,
                Help: "Caps generated token count. Leave blank to use server default."),
        ];
    }
}
