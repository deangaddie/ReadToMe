using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Deserializes a step's settings payload. Malformed JSON is a warning and a fall back to the
    /// step's defaults, never a throw — a step must not fail an item over its own config.
    /// </summary>
    internal static class StepSettingsJson
    {
        public static TSettings? Parse<TSettings>(string? settingsJson, string stepId, ILogger logger)
            where TSettings : class
        {
            if (string.IsNullOrWhiteSpace(settingsJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<TSettings>(settingsJson, AudioPostProcessJson.Options);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "{StepId} settings JSON malformed; using defaults", stepId);
                return null;
            }
        }
    }
}
