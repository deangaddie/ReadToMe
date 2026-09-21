using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Read2Me.Services.Audio.Transcription.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Rewrites a provider config's <c>settingsJson</c> into the exact text the Blazor config forms
    /// store (Angular ticket 22): read leniently into the provider's <c>*Settings</c> record, written
    /// with the serializer options that provider's form uses. Whatever key case or order a client
    /// sends, both UIs leave the same string behind — and the services, which read these blobs
    /// case-sensitively, always find their keys.
    /// </summary>
    public static class ProviderSettingsJson
    {
        private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };

        // VoiceDesignServiceConfigForm writes the VoxCPM2 record with the web defaults; every other form uses the plain ones.
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        /// <exception cref="JsonException">The text is not a JSON object of the provider's shape.</exception>
        public static void Canonicalize(ParagraphTtsServiceConfig config) => config.SettingsJson = config.Type switch
        {
            ParagraphTtsServiceType.VoxCpm2 => Rewrite(config.SettingsJson, VoxCpm2ParagraphTtsSettings.Recommended),
            ParagraphTtsServiceType.Chatterbox => Rewrite(config.SettingsJson, ChatterboxParagraphTtsSettings.Recommended),
            ParagraphTtsServiceType.ChatterboxTurbo => Rewrite(config.SettingsJson, ChatterboxTurboParagraphTtsSettings.Recommended),
            ParagraphTtsServiceType.Qwen3Base => Rewrite(config.SettingsJson, Qwen3ParagraphTtsSettings.Recommended),
            _ => config.SettingsJson,
        };

        /// <inheritdoc cref="Canonicalize(ParagraphTtsServiceConfig)"/>
        public static void Canonicalize(VoiceDesignServiceConfig config) => config.SettingsJson = config.Type switch
        {
            VoiceDesignServiceType.VoxCpm2 => Rewrite(config.SettingsJson, VoxCpm2VoiceDesignSettings.Recommended, Web),
            VoiceDesignServiceType.Qwen3 => Rewrite(config.SettingsJson, new Qwen3VoiceDesignSettings()),
            _ => config.SettingsJson,
        };

        /// <inheritdoc cref="Canonicalize(ParagraphTtsServiceConfig)"/>
        public static void Canonicalize(TranscriptionServiceConfig config) => config.SettingsJson = config.Type switch
        {
            TranscriptionServiceType.LocalWhisper => Rewrite(config.SettingsJson, new LocalWhisperSettings()),
            _ => config.SettingsJson,
        };

        /// <inheritdoc cref="Canonicalize(ParagraphTtsServiceConfig)"/>
        public static void Canonicalize(SemanticSimilarityServiceConfig config) =>
            config.SettingsJson = Rewrite(config.SettingsJson, new SemanticSimilaritySettings());

        /// <summary>The similarity settings of a stored config; the record's defaults when the blob is blank.</summary>
        public static SemanticSimilaritySettings ReadSimilarity(SemanticSimilarityServiceConfig config) =>
            Read(config.SettingsJson, new SemanticSimilaritySettings());

        /// <param name="blank">What a blank blob means — the same starting point the provider's Blazor form takes.</param>
        private static string Rewrite<TSettings>(string json, TSettings blank, JsonSerializerOptions? write = null)
            where TSettings : class =>
            JsonSerializer.Serialize(Read(json, blank), write);

        private static TSettings Read<TSettings>(string json, TSettings blank) where TSettings : class =>
            string.IsNullOrWhiteSpace(json)
                ? blank
                : JsonSerializer.Deserialize<TSettings>(json, Lenient) ?? blank;
    }
}
