using System;
using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;

namespace Read2Me.App.Shared
{
    public sealed class ParagraphTtsServiceConfigForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ParagraphTtsServiceType Type { get; set; } = ParagraphTtsServiceType.VoxCpm2;

        // VoxCpm2 connection/app fields owned by the form (not the editor).
        public string BaseUrl { get; set; } = "";
        public int MaxChunkChars { get; set; } = 500;

        // VoxCpm2 tunable params — full JSON the editor binds to (BaseUrl/MaxChunkChars held separately above).
        public string? SettingsJson { get; set; }

        public static ParagraphTtsServiceConfigForm FromConfig(ParagraphTtsServiceConfig c)
        {
            var form = new ParagraphTtsServiceConfigForm
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type,
            };

            switch (c.Type)
            {
                case ParagraphTtsServiceType.VoxCpm2:
                    var s = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? VoxCpm2ParagraphTtsSettings.Recommended
                        : JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(c.SettingsJson) ?? VoxCpm2ParagraphTtsSettings.Recommended;
                    form.BaseUrl = s.BaseUrl;
                    form.MaxChunkChars = s.MaxChunkChars;
                    form.SettingsJson = JsonSerializer.Serialize(s);
                    break;
            }

            return form;
        }

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "Name is required.";

            switch (Type)
            {
                case ParagraphTtsServiceType.VoxCpm2:
                    if (string.IsNullOrWhiteSpace(BaseUrl))
                        return "Base URL is required.";
                    if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
                        return "Base URL must be a valid absolute URL (e.g. http://localhost:8000).";
                    break;
            }

            return null;
        }

        public ParagraphTtsServiceConfig BuildConfig()
        {
            var settingsJson = Type switch
            {
                ParagraphTtsServiceType.VoxCpm2 => BuildVoxCpm2SettingsJson(),
                _ => throw new NotSupportedException($"Unsupported paragraph TTS type '{Type}'."),
            };

            return new ParagraphTtsServiceConfig
            {
                Id = Id,
                Name = Name.Trim(),
                Type = Type,
                SettingsJson = settingsJson,
            };
        }

        // BaseUrl + MaxChunkChars owned by the form, the 9 tunable params by the editor's SettingsJson — merge here.
        private string BuildVoxCpm2SettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? VoxCpm2ParagraphTtsSettings.Recommended
                : JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(SettingsJson)
                  ?? VoxCpm2ParagraphTtsSettings.Recommended;

            settings = settings with { BaseUrl = BaseUrl.Trim(), MaxChunkChars = MaxChunkChars };
            return JsonSerializer.Serialize(settings);
        }
    }
}
