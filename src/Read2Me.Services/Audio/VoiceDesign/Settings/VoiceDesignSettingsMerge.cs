using System.Text.Json;
using System.Text.Json.Nodes;

namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>
    /// Merges a per-voice settings override (partial JSON) over a config's default
    /// settings JSON, then deserializes the result into the typed settings record.
    /// Only keys present in the override replace the defaults.
    /// </summary>
    public static class VoiceDesignSettingsMerge
    {
        public static T Merge<T>(string defaultsJson, string? overrideJson)
        {
            var baseObj = string.IsNullOrWhiteSpace(defaultsJson)
                ? new JsonObject()
                : (JsonNode.Parse(defaultsJson) as JsonObject ?? new JsonObject());

            if (!string.IsNullOrWhiteSpace(overrideJson)
                && JsonNode.Parse(overrideJson) is JsonObject overrideObj)
            {
                foreach (var kvp in overrideObj)
                    baseObj[kvp.Key] = kvp.Value?.DeepClone();
            }

            return baseObj.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }
}
