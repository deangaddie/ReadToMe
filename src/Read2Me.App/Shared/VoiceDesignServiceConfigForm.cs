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

        // Qwen3 settings. ModelId and Seed ride along as loaded (the Angular app edits them).
        public string ModelId { get; set; } = Qwen3VoiceDesignSettings.Recommended.ModelId;
        public int? Seed { get; set; }
        public string Language { get; set; } = "auto";
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? TopK { get; set; }
        public double? RepetitionPenalty { get; set; }
        public int? MaxNewTokens { get; set; }

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
                        ? Qwen3VoiceDesignSettings.Recommended
                        : JsonSerializer.Deserialize<Qwen3VoiceDesignSettings>(c.SettingsJson) ?? Qwen3VoiceDesignSettings.Recommended;
                    form.BaseUrl = q3.BaseUrl;
                    form.ModelId = q3.ModelId;
                    form.Seed = q3.Seed;
                    form.Language = q3.Language;
                    form.Temperature = q3.Temperature;
                    form.TopP = q3.TopP;
                    form.TopK = q3.TopK;
                    form.RepetitionPenalty = q3.RepetitionPenalty;
                    form.MaxNewTokens = q3.MaxNewTokens;
                    break;

                // Legacy UI: connection only; Breeze tuning lives in the Angular app.
                case VoiceDesignServiceType.Breeze:
                    var br = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? BreezeVoiceDesignSettings.Recommended
                        : JsonSerializer.Deserialize<BreezeVoiceDesignSettings>(c.SettingsJson) ?? BreezeVoiceDesignSettings.Recommended;
                    form.BaseUrl = br.BaseUrl;
                    form.SettingsJson = JsonSerializer.Serialize(br);
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
                return "Base URL must be a valid absolute URL (e.g. http://localhost:8004).";

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
                        ModelId = ModelId,
                        Language = Language,
                        Temperature = Temperature,
                        TopP = TopP,
                        TopK = TopK,
                        RepetitionPenalty = RepetitionPenalty,
                        MaxNewTokens = MaxNewTokens,
                        Seed = Seed,
                    }),
                VoiceDesignServiceType.Breeze => BuildBreezeSettingsJson(),
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

        // BaseUrl owned by the form; the tuning keys are kept as loaded (Angular edits them).
        private string BuildBreezeSettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? BreezeVoiceDesignSettings.Recommended
                : JsonSerializer.Deserialize<BreezeVoiceDesignSettings>(SettingsJson)
                  ?? BreezeVoiceDesignSettings.Recommended;

            return JsonSerializer.Serialize(settings with { BaseUrl = BaseUrl.Trim() });
        }
    }
}
