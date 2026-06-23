using System.Text.Json;
using System.Text.Json.Nodes;

namespace Read2Me.Services.Audio.VoiceDesign.Settings
{
    public static class VoxCpm2SettingsDiff
    {
        private static readonly JsonSerializerOptions WebOptions =
            new(JsonSerializerDefaults.Web);

        public static string Diff(string baseJson, VoxCpm2VoiceDesignSettings edited)
        {
            var baseObj = string.IsNullOrWhiteSpace(baseJson)
                ? new JsonObject()
                : (JsonNode.Parse(baseJson) as JsonObject ?? new JsonObject());

            var editedJson = JsonSerializer.Serialize(edited, WebOptions);
            var editedObj = JsonNode.Parse(editedJson) as JsonObject ?? new JsonObject();

            var result = new JsonObject();
            foreach (var kvp in editedObj)
            {
                if (kvp.Key == "baseUrl") continue;

                var baseVal = baseObj[kvp.Key];
                var editedVal = kvp.Value;

                if (!JsonNode.DeepEquals(baseVal, editedVal))
                    result[kvp.Key] = editedVal?.DeepClone();
            }

            return result.ToJsonString();
        }

        public static VoxCpm2VoiceDesignSettings Apply(string baseJson, string? patchJson)
            => VoiceDesignSettingsMerge.Merge<VoxCpm2VoiceDesignSettings>(baseJson, patchJson);
    }
}
