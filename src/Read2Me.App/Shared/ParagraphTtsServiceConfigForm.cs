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

        // VoxCpm2 settings.
        public string BaseUrl { get; set; } = "";
        public int MaxLen { get; set; } = 4096;

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
                        ? new VoxCpm2ParagraphTtsSettings()
                        : JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(c.SettingsJson) ?? new VoxCpm2ParagraphTtsSettings();
                    form.BaseUrl = s.BaseUrl;
                    form.MaxLen = s.MaxLen;
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
                ParagraphTtsServiceType.VoxCpm2 =>
                    JsonSerializer.Serialize(new VoxCpm2ParagraphTtsSettings { BaseUrl = BaseUrl.Trim(), MaxLen = MaxLen }),
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
    }
}
