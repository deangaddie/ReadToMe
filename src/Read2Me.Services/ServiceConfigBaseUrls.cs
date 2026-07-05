using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Read2Me.Services.Audio.Transcription.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services
{
    /// <summary>
    /// Extracts the backend base URL from a service config. Non-LLM configs keep their URL inside
    /// <c>SettingsJson</c>, shaped per <c>Type</c>; this is the single place that mapping lives.
    /// Returns null when the config has no URL-bearing settings or the JSON is malformed.
    /// </summary>
    public static class ServiceConfigBaseUrls
    {
        public static string? For(LlmServerConfig config) =>
            NullIfBlank(config.BaseUrl);

        public static string? For(ParagraphTtsServiceConfig config) =>
            config.Type switch
            {
                ParagraphTtsServiceType.VoxCpm2 => NullIfBlank(Parse<VoxCpm2ParagraphTtsSettings>(config.SettingsJson)?.BaseUrl),
                _ => null,
            };

        public static string? For(TranscriptionServiceConfig config) =>
            config.Type switch
            {
                TranscriptionServiceType.LocalWhisper => NullIfBlank(Parse<LocalWhisperSettings>(config.SettingsJson)?.BaseUrl),
                _ => null,
            };

        public static string? For(VoiceDesignServiceConfig config) =>
            config.Type switch
            {
                VoiceDesignServiceType.VoxCpm2 => NullIfBlank(Parse<VoxCpm2VoiceDesignSettings>(config.SettingsJson)?.BaseUrl),
                VoiceDesignServiceType.Qwen3 => NullIfBlank(Parse<Qwen3VoiceDesignSettings>(config.SettingsJson)?.BaseUrl),
                _ => null,
            };

        public static string? For(SemanticSimilarityServiceConfig config) =>
            NullIfBlank(Parse<SemanticSimilaritySettings>(config.SettingsJson)?.BaseUrl);

        private static T? Parse<T>(string json) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
