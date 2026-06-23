using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoxCpm2SettingsDiffTests
    {
        private static readonly string RecommendedJson =
            JsonSerializer.Serialize(VoxCpm2VoiceDesignSettings.Recommended,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        [Fact]
        public void Recommended_HasConfirmedDefaultValues()
        {
            var r = VoxCpm2VoiceDesignSettings.Recommended;

            Assert.Equal(2.0, r.CfgValue);
            Assert.Equal(10, r.InferenceTimesteps);
            Assert.Equal(2, r.MinLen);
            Assert.Equal(4096, r.MaxLen);
            Assert.False(r.Normalize);
            Assert.False(r.Denoise);
            Assert.True(r.RetryBadcase);
            Assert.Equal(3, r.RetryBadcaseMaxTimes);
            Assert.Equal(6.0, r.RetryBadcaseRatioThreshold);
        }

        [Fact]
        public void Diff_UnchangedEdited_ReturnsEmptyObject()
        {
            var diff = VoxCpm2SettingsDiff.Diff(RecommendedJson, VoxCpm2VoiceDesignSettings.Recommended);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Empty(obj);
        }

        [Fact]
        public void Diff_OneDifferentField_ReturnsOnlyThatKey()
        {
            var edited = VoxCpm2VoiceDesignSettings.Recommended with { CfgValue = 3.5 };
            var diff = VoxCpm2SettingsDiff.Diff(RecommendedJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Single(obj);
            Assert.True(obj.ContainsKey("cfg_value"));
            Assert.Equal(3.5, obj["cfg_value"]!.GetValue<double>());
        }

        [Fact]
        public void Diff_SeveralDifferentFields_ReturnsExactlyThoseKeys()
        {
            var edited = VoxCpm2VoiceDesignSettings.Recommended with
            {
                InferenceTimesteps = 25,
                Normalize = true,
                RetryBadcaseMaxTimes = 7,
            };
            var diff = VoxCpm2SettingsDiff.Diff(RecommendedJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Equal(3, obj.Count);
            Assert.True(obj.ContainsKey("inference_timesteps"));
            Assert.True(obj.ContainsKey("normalize"));
            Assert.True(obj.ContainsKey("retry_badcase_max_times"));
        }

        [Fact]
        public void Diff_NeverIncludesBaseUrl_EvenIfItDiffers()
        {
            var edited = VoxCpm2VoiceDesignSettings.Recommended with { BaseUrl = "http://other:9999" };
            var baseJson = JsonSerializer.Serialize(
                VoxCpm2VoiceDesignSettings.Recommended with { BaseUrl = "http://original:8003" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var diff = VoxCpm2SettingsDiff.Diff(baseJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.DoesNotContain("baseUrl", obj.Select(kvp => kvp.Key));
            Assert.DoesNotContain("base_url", obj.Select(kvp => kvp.Key));
        }

        [Fact]
        public void RoundTrip_MergeOfDiff_EqualsEdited()
        {
            var edited = VoxCpm2VoiceDesignSettings.Recommended with
            {
                CfgValue = 4.0,
                MinLen = 5,
                RetryBadcase = false,
            };

            var diff = VoxCpm2SettingsDiff.Diff(RecommendedJson, edited);
            var restored = VoiceDesignSettingsMerge.Merge<VoxCpm2VoiceDesignSettings>(RecommendedJson, diff);

            Assert.Equal(edited.CfgValue, restored.CfgValue);
            Assert.Equal(edited.InferenceTimesteps, restored.InferenceTimesteps);
            Assert.Equal(edited.MinLen, restored.MinLen);
            Assert.Equal(edited.MaxLen, restored.MaxLen);
            Assert.Equal(edited.Normalize, restored.Normalize);
            Assert.Equal(edited.Denoise, restored.Denoise);
            Assert.Equal(edited.RetryBadcase, restored.RetryBadcase);
            Assert.Equal(edited.RetryBadcaseMaxTimes, restored.RetryBadcaseMaxTimes);
            Assert.Equal(edited.RetryBadcaseRatioThreshold, restored.RetryBadcaseRatioThreshold);
        }

        [Fact]
        public void Apply_EquivalentToMerge()
        {
            const string patch = """{"cfg_value":3.0,"inference_timesteps":20}""";

            var viaApply = VoxCpm2SettingsDiff.Apply(RecommendedJson, patch);
            var viaMerge = VoiceDesignSettingsMerge.Merge<VoxCpm2VoiceDesignSettings>(RecommendedJson, patch);

            Assert.Equal(viaMerge.CfgValue, viaApply.CfgValue);
            Assert.Equal(viaMerge.InferenceTimesteps, viaApply.InferenceTimesteps);
        }

        [Fact]
        public void RoundTrip_MergeOfDiff_WithConfiguredProviderBase_EqualsEdited()
        {
            // Simulate a provider config that differs from Recommended (e.g. admin tuned cfgValue=3.0, inferenceTimesteps=20)
            var configuredBase = VoxCpm2VoiceDesignSettings.Recommended with { CfgValue = 3.0, InferenceTimesteps = 20 };
            var configuredBaseJson = JsonSerializer.Serialize(configuredBase, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            // User overrides only MinLen
            var edited = configuredBase with { MinLen = 8 };

            var diff = VoxCpm2SettingsDiff.Diff(configuredBaseJson, edited);

            // Diff should only contain min_len — not cfg_value or inference_timesteps (they match the base)
            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Single(obj);
            Assert.True(obj.ContainsKey("min_len"));

            // Round-trip: Merge(base, diff) == edited
            var restored = VoiceDesignSettingsMerge.Merge<VoxCpm2VoiceDesignSettings>(configuredBaseJson, diff);
            Assert.Equal(edited.CfgValue, restored.CfgValue);
            Assert.Equal(edited.InferenceTimesteps, restored.InferenceTimesteps);
            Assert.Equal(edited.MinLen, restored.MinLen);
            Assert.Equal(edited.MaxLen, restored.MaxLen);
            Assert.Equal(edited.Normalize, restored.Normalize);
            Assert.Equal(edited.Denoise, restored.Denoise);
            Assert.Equal(edited.RetryBadcase, restored.RetryBadcase);
            Assert.Equal(edited.RetryBadcaseMaxTimes, restored.RetryBadcaseMaxTimes);
            Assert.Equal(edited.RetryBadcaseRatioThreshold, restored.RetryBadcaseRatioThreshold);
        }
    }
}
