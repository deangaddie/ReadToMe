using System.Text.Json;
using System.Text.Json.Nodes;

namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    /// <summary>A per-voice override for Breeze voice design: only the keys that differ from the config.</summary>
    public static class BreezeVoiceDesignSettingsDiff
    {
        public static string Diff(string baseJson, BreezeVoiceDesignSettings edited)
        {
            var baseObj = string.IsNullOrWhiteSpace(baseJson)
                ? new JsonObject()
                : (JsonNode.Parse(baseJson) as JsonObject ?? new JsonObject());

            var editedObj = JsonSerializer.SerializeToNode(edited) as JsonObject ?? new JsonObject();

            var result = new JsonObject();
            foreach (var kvp in editedObj)
            {
                if (kvp.Key == "baseUrl") continue;

                if (!JsonNode.DeepEquals(baseObj[kvp.Key], kvp.Value))
                    result[kvp.Key] = kvp.Value?.DeepClone();
            }

            return result.ToJsonString();
        }

        public static BreezeVoiceDesignSettings Apply(string baseJson, string? patchJson)
            => VoiceDesignSettingsMerge.Merge<BreezeVoiceDesignSettings>(baseJson, patchJson);
    }
}
