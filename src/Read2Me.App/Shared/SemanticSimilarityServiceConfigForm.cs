using System;
using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;

namespace Read2Me.App.Shared
{
    public sealed class SemanticSimilarityServiceConfigForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public SemanticSimilarityServiceType Type { get; set; } = SemanticSimilarityServiceType.MiniLmL6;
        public string BaseUrl { get; set; } = "";
        public double PassThreshold { get; set; } = 0.85;

        public static SemanticSimilarityServiceConfigForm FromConfig(SemanticSimilarityServiceConfig c)
        {
            var settings = string.IsNullOrWhiteSpace(c.SettingsJson)
                ? new SemanticSimilaritySettings()
                : JsonSerializer.Deserialize<SemanticSimilaritySettings>(c.SettingsJson) ?? new SemanticSimilaritySettings();

            return new SemanticSimilarityServiceConfigForm
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type,
                BaseUrl = settings.BaseUrl,
                PassThreshold = settings.PassThreshold,
            };
        }

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "Name is required.";

            if (string.IsNullOrWhiteSpace(BaseUrl))
                return "Base URL is required.";

            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
                return "Base URL must be a valid absolute URL (e.g. http://localhost:8200).";

            if (PassThreshold < 0 || PassThreshold > 1)
                return "Pass threshold must be between 0 and 1 (exclusive).";

            return null;
        }

        public SemanticSimilarityServiceConfig BuildConfig()
        {
            return new SemanticSimilarityServiceConfig
            {
                Id = Id,
                Name = Name.Trim(),
                Type = Type,
                SettingsJson = JsonSerializer.Serialize(new SemanticSimilaritySettings
                {
                    BaseUrl = BaseUrl.Trim(),
                    PassThreshold = PassThreshold,
                }),
            };
        }
    }
}
