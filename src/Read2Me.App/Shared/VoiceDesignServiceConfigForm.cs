using System;
using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// Edit-state for a <see cref="VoiceDesignServiceConfig"/>. The selected
    /// <see cref="Type"/> determines which fields are relevant; type-specific
    /// fields are serialized into the config's settings blob on build.
    /// </summary>
    public sealed class VoiceDesignServiceConfigForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public VoiceDesignServiceType Type { get; set; } = VoiceDesignServiceType.VoxCpm2;

        // Shared
        public string BaseUrl { get; set; } = "";

        // VoxCpm2 settings — full JSON for the tunable fields (BaseUrl held separately above)
        public string? SettingsJson { get; set; }

        // Qwen3 settings
        public string? ApiKey { get; set; }
        public string? Model { get; set; }

        private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

        public static VoiceDesignServiceConfigForm FromConfig(VoiceDesignServiceConfig c)
        {
            var form = new VoiceDesignServiceConfigForm
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type,
            };

            switch (c.Type)
            {
                case VoiceDesignServiceType.VoxCpm2:
                    var vox = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? VoxCpm2VoiceDesignSettings.Recommended
                        : JsonSerializer.Deserialize<VoxCpm2VoiceDesignSettings>(c.SettingsJson, _jsonOpts)
                          ?? VoxCpm2VoiceDesignSettings.Recommended;
                    form.BaseUrl = vox.BaseUrl;
                    form.SettingsJson = JsonSerializer.Serialize(vox, _jsonOpts);
                    break;
                case VoiceDesignServiceType.Qwen3:
                    var q3 = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? new Qwen3VoiceDesignSettings()
                        : JsonSerializer.Deserialize<Qwen3VoiceDesignSettings>(c.SettingsJson) ?? new Qwen3VoiceDesignSettings();
                    form.BaseUrl = q3.BaseUrl;
                    form.ApiKey = q3.ApiKey;
                    form.Model = q3.Model;
                    break;
            }

            return form;
        }

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "Name is required.";

            if (string.IsNullOrWhiteSpace(BaseUrl))
                return "Base URL is required.";
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
                return "Base URL must be a valid absolute URL (e.g. http://localhost:8003).";

            return null;
        }

        public VoiceDesignServiceConfig BuildConfig()
        {
            var settingsJson = Type switch
            {
                VoiceDesignServiceType.VoxCpm2 => BuildVoxCpm2SettingsJson(),
                VoiceDesignServiceType.Qwen3 =>
                    JsonSerializer.Serialize(new Qwen3VoiceDesignSettings
                    {
                        BaseUrl = BaseUrl.Trim(),
                        ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
                        Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
                    }),
                _ => throw new NotSupportedException($"Unsupported voice design type '{Type}'."),
            };

            return new VoiceDesignServiceConfig
            {
                Id = Id,
                Name = Name.Trim(),
                Type = Type,
                SettingsJson = settingsJson,
            };
        }

        private string BuildVoxCpm2SettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? VoxCpm2VoiceDesignSettings.Recommended
                : JsonSerializer.Deserialize<VoxCpm2VoiceDesignSettings>(SettingsJson, _jsonOpts)
                  ?? VoxCpm2VoiceDesignSettings.Recommended;

            // BaseUrl owned by the form, not the editor — merge in here
            settings = settings with { BaseUrl = BaseUrl.Trim() };
            return JsonSerializer.Serialize(settings, _jsonOpts);
        }
    }
}
