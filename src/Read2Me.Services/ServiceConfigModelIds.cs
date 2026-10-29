using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services
{
    /// <summary>
    /// The model a config names: an LLM config's <c>Model</c> (a llama preset), or the audio.cpp model
    /// id (an entry in <c>server.json</c>) in a TTS or voice-design config's <c>SettingsJson</c>,
    /// shaped per <c>Type</c>, as <see cref="ServiceConfigBaseUrls"/> does the URL. Returns null when
    /// the JSON is malformed or the model is blank.
    /// </summary>
    public static class ServiceConfigModelIds
    {
        public static string? For(LlmServerConfig config) =>
            NullIfBlank(config.Model);

        public static string? For(ParagraphTtsServiceConfig config) =>
            config.Type switch
            {
                ParagraphTtsServiceType.VoxCpm2 => NullIfBlank(ServiceConfigBaseUrls.Parse<VoxCpm2ParagraphTtsSettings>(config.SettingsJson)?.ModelId),
                ParagraphTtsServiceType.Chatterbox => NullIfBlank(ServiceConfigBaseUrls.Parse<ChatterboxParagraphTtsSettings>(config.SettingsJson)?.ModelId),
                ParagraphTtsServiceType.Qwen3Base => NullIfBlank(ServiceConfigBaseUrls.Parse<Qwen3ParagraphTtsSettings>(config.SettingsJson)?.ModelId),
                ParagraphTtsServiceType.Breeze => NullIfBlank(ServiceConfigBaseUrls.Parse<BreezeParagraphTtsSettings>(config.SettingsJson)?.ModelId),
                _ => null,
            };

        public static string? For(VoiceDesignServiceConfig config) =>
            config.Type switch
            {
                VoiceDesignServiceType.VoxCpm2 => NullIfBlank(ServiceConfigBaseUrls.Parse<VoxCpm2VoiceDesignSettings>(config.SettingsJson)?.ModelId),
                VoiceDesignServiceType.Qwen3 => NullIfBlank(ServiceConfigBaseUrls.Parse<Qwen3VoiceDesignSettings>(config.SettingsJson)?.ModelId),
                VoiceDesignServiceType.Breeze => NullIfBlank(ServiceConfigBaseUrls.Parse<BreezeVoiceDesignSettings>(config.SettingsJson)?.ModelId),
                _ => null,
            };

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
