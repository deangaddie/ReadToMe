using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.ParagraphTts.Settings
{
    public static class VoxCpm2ParagraphTtsSettingsDiff
    {
        private static readonly JsonSerializerOptions WebOptions =
            new(JsonSerializerDefaults.Web);

        public static string Diff(string baseJson, VoxCpm2ParagraphTtsSettings edited)
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
                if (kvp.Key == "maxChunkChars") continue;
                if (kvp.Key == "carrierPrefixEnabled") continue;
                if (kvp.Key == "carrierMaxTargetChars") continue;

                var baseVal = baseObj[kvp.Key];
                var editedVal = kvp.Value;

                if (!JsonNode.DeepEquals(baseVal, editedVal))
                    result[kvp.Key] = editedVal?.DeepClone();
            }

            return result.ToJsonString();
        }

        public static VoxCpm2ParagraphTtsSettings Apply(string baseJson, string? patchJson)
            => VoiceDesignSettingsMerge.Merge<VoxCpm2ParagraphTtsSettings>(baseJson, patchJson);
    }
}
