using System;
using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.Transcription.Settings;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// Edit-state for a <see cref="TranscriptionServiceConfig"/>. The selected
    /// <see cref="Type"/> determines which fields are relevant; type-specific
    /// fields are serialized into the config's settings blob on build.
    /// </summary>
    public sealed class TranscriptionServiceConfigForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public TranscriptionServiceType Type { get; set; } = TranscriptionServiceType.LocalWhisper;

        // LocalWhisper settings.
        public string BaseUrl { get; set; } = "";

        public static TranscriptionServiceConfigForm FromConfig(TranscriptionServiceConfig c)
        {
            var form = new TranscriptionServiceConfigForm
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type,
            };

            switch (c.Type)
            {
                case TranscriptionServiceType.LocalWhisper:
                    var s = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? new LocalWhisperSettings()
                        : JsonSerializer.Deserialize<LocalWhisperSettings>(c.SettingsJson) ?? new LocalWhisperSettings();
                    form.BaseUrl = s.BaseUrl;
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
                case TranscriptionServiceType.LocalWhisper:
                    if (string.IsNullOrWhiteSpace(BaseUrl))
                        return "Base URL is required.";
                    if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
                        return "Base URL must be a valid absolute URL (e.g. http://localhost:9000).";
                    break;
            }

            return null;
        }

        public TranscriptionServiceConfig BuildConfig()
        {
            var settingsJson = Type switch
            {
                TranscriptionServiceType.LocalWhisper =>
                    JsonSerializer.Serialize(new LocalWhisperSettings { BaseUrl = BaseUrl.Trim() }),
                _ => throw new NotSupportedException($"Unsupported transcription type '{Type}'."),
            };

            return new TranscriptionServiceConfig
            {
                Id = Id,
                Name = Name.Trim(),
                Type = Type,
                SettingsJson = settingsJson,
            };
        }
    }
}
