using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoxCpm2ParagraphTtsSettingsDiffTests
    {
        private static readonly string RecommendedJson =
            JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        [Fact]
        public void Recommended_HasConfirmedDefaultValues()
        {
            var r = VoxCpm2ParagraphTtsSettings.Recommended;

            Assert.Equal(2.0, r.CfgValue);
            Assert.Equal(10, r.InferenceTimesteps);
            Assert.Equal(2, r.MinLen);
            Assert.Equal(4096, r.MaxLen);
            Assert.False(r.Normalize);
            Assert.False(r.Denoise);
            Assert.True(r.RetryBadcase);
            Assert.Equal(3, r.RetryBadcaseMaxTimes);
            Assert.Equal(6.0, r.RetryBadcaseRatioThreshold);
            Assert.Equal(string.Empty, r.BaseUrl);
            Assert.Equal(500, r.MaxChunkChars);
        }

        [Fact]
        public void Diff_UnchangedEdited_ReturnsEmptyObject()
        {
            var diff = VoxCpm2ParagraphTtsSettingsDiff.Diff(RecommendedJson, VoxCpm2ParagraphTtsSettings.Recommended);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Empty(obj);
        }

        [Fact]
        public void Diff_OneDifferentField_ReturnsOnlyThatKey()
        {
            var edited = VoxCpm2ParagraphTtsSettings.Recommended with { CfgValue = 3.5 };
            var diff = VoxCpm2ParagraphTtsSettingsDiff.Diff(RecommendedJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Single(obj);
            Assert.True(obj.ContainsKey("cfg_value"));
            Assert.Equal(3.5, obj["cfg_value"]!.GetValue<double>());
        }

        [Fact]
        public void Diff_SeveralDifferentFields_ReturnsExactlyThoseKeys()
        {
            var edited = VoxCpm2ParagraphTtsSettings.Recommended with
            {
                InferenceTimesteps = 25,
                Normalize = true,
                RetryBadcaseMaxTimes = 7,
            };
            var diff = VoxCpm2ParagraphTtsSettingsDiff.Diff(RecommendedJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Equal(3, obj.Count);
            Assert.True(obj.ContainsKey("inference_timesteps"));
            Assert.True(obj.ContainsKey("normalize"));
            Assert.True(obj.ContainsKey("retry_badcase_max_times"));
        }

        [Fact]
        public void Diff_NeverIncludesBaseUrl_EvenIfItDiffers()
        {
            var edited = VoxCpm2ParagraphTtsSettings.Recommended with { BaseUrl = "http://other:9999" };
            var baseJson = JsonSerializer.Serialize(
                VoxCpm2ParagraphTtsSettings.Recommended with { BaseUrl = "http://original:8000" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var diff = VoxCpm2ParagraphTtsSettingsDiff.Diff(baseJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.DoesNotContain("baseUrl", obj.Select(kvp => kvp.Key));
            Assert.DoesNotContain("base_url", obj.Select(kvp => kvp.Key));
        }

        [Fact]
        public void Diff_NeverIncludesMaxChunkChars_EvenIfItDiffers()
        {
            var edited = VoxCpm2ParagraphTtsSettings.Recommended with { MaxChunkChars = 999 };
            var baseJson = JsonSerializer.Serialize(
                VoxCpm2ParagraphTtsSettings.Recommended with { MaxChunkChars = 250 },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var diff = VoxCpm2ParagraphTtsSettingsDiff.Diff(baseJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.DoesNotContain("maxChunkChars", obj.Select(kvp => kvp.Key));
            Assert.DoesNotContain("max_chunk_chars", obj.Select(kvp => kvp.Key));
        }

        [Fact]
        public void Apply_ReconstructsFullObject_ChangedFromPatch_RestFromBase()
        {
            const string patch = """{"cfg_value":3.5}""";

            var restored = VoxCpm2ParagraphTtsSettingsDiff.Apply(RecommendedJson, patch);

            Assert.Equal(3.5, restored.CfgValue);            // from patch
            Assert.Equal(10, restored.InferenceTimesteps);   // from base
            Assert.Equal(2, restored.MinLen);
            Assert.Equal(4096, restored.MaxLen);
            Assert.False(restored.Normalize);
            Assert.False(restored.Denoise);
            Assert.True(restored.RetryBadcase);
            Assert.Equal(3, restored.RetryBadcaseMaxTimes);
            Assert.Equal(6.0, restored.RetryBadcaseRatioThreshold);
            Assert.Equal(500, restored.MaxChunkChars);
        }

        [Fact]
        public void RoundTrip_ApplyOfDiff_EqualsEdited()
        {
            var edited = VoxCpm2ParagraphTtsSettings.Recommended with
            {
                CfgValue = 4.0,
                MinLen = 5,
                RetryBadcase = false,
            };

            var diff = VoxCpm2ParagraphTtsSettingsDiff.Diff(RecommendedJson, edited);
            var restored = VoxCpm2ParagraphTtsSettingsDiff.Apply(RecommendedJson, diff);

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
