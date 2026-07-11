using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// One entry in the ordered post-process step list stored on the settings row.
    /// <see cref="Settings"/> is the step's opaque settings payload (shape is owned by the
    /// step, e.g. <see cref="ConsonantSoftenSettings"/> for consonant-soften).
    /// </summary>
    public sealed record AudioPostProcessStepConfig(string StepId, bool Enabled, JsonElement? Settings)
    {
        public static AudioPostProcessStepConfig Create<TSettings>(string stepId, bool enabled, TSettings settings) =>
            new(stepId, enabled, JsonSerializer.SerializeToElement(settings, AudioPostProcessJson.Options));

        /// <summary>Deserializes <see cref="Settings"/>, or null when absent/malformed.</summary>
        public TSettings? GetSettings<TSettings>() where TSettings : class
        {
            if (Settings is not { } element) return null;
            try
            {
                return element.Deserialize<TSettings>(AudioPostProcessJson.Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Raw settings JSON as passed to the step, or null when absent.</summary>
        public string? SettingsJson => Settings?.GetRawText();
    }

    /// <summary>Serializer options shared by everything touching the step-config JSON.</summary>
    public static class AudioPostProcessJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
