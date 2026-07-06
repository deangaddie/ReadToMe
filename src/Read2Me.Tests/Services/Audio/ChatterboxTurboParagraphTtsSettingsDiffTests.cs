using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ChatterboxTurboParagraphTtsSettingsDiffTests
    {
        private static readonly string RecommendedJson =
            JsonSerializer.Serialize(ChatterboxTurboParagraphTtsSettings.Recommended,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        [Fact]
        public void Recommended_HasConfirmedDefaultValues()
        {
            var r = ChatterboxTurboParagraphTtsSettings.Recommended;

            Assert.Equal(0.8, r.Temperature);
            Assert.Equal(1.2, r.RepetitionPenalty);
            Assert.Equal(string.Empty, r.BaseUrl);
            Assert.Equal(500, r.MaxChunkChars);
        }

        [Fact]
        public void Diff_UnchangedEdited_ReturnsEmptyObject()
        {
            var diff = ChatterboxTurboParagraphTtsSettingsDiff.Diff(RecommendedJson, ChatterboxTurboParagraphTtsSettings.Recommended);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Empty(obj);
        }

        [Fact]
        public void Diff_OneDifferentField_ReturnsOnlyThatKey()
        {
            var edited = ChatterboxTurboParagraphTtsSettings.Recommended with { Temperature = 0.7 };
            var diff = ChatterboxTurboParagraphTtsSettingsDiff.Diff(RecommendedJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Single(obj);
            Assert.True(obj.ContainsKey("temperature"));
            Assert.Equal(0.7, obj["temperature"]!.GetValue<double>());
        }

        [Fact]
        public void Diff_NeverIncludesBaseUrl_EvenIfItDiffers()
        {
            var edited = ChatterboxTurboParagraphTtsSettings.Recommended with { BaseUrl = "http://other:9999" };
            var baseJson = JsonSerializer.Serialize(
                ChatterboxTurboParagraphTtsSettings.Recommended with { BaseUrl = "http://original:8001" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var diff = ChatterboxTurboParagraphTtsSettingsDiff.Diff(baseJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.DoesNotContain("baseUrl", obj.Select(kvp => kvp.Key));
        }

        [Fact]
        public void Diff_NeverIncludesMaxChunkChars_EvenIfItDiffers()
        {
            var edited = ChatterboxTurboParagraphTtsSettings.Recommended with { MaxChunkChars = 999 };
            var baseJson = JsonSerializer.Serialize(
                ChatterboxTurboParagraphTtsSettings.Recommended with { MaxChunkChars = 250 },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var diff = ChatterboxTurboParagraphTtsSettingsDiff.Diff(baseJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.DoesNotContain("maxChunkChars", obj.Select(kvp => kvp.Key));
        }

        [Fact]
        public void Apply_ReconstructsFullObject_ChangedFromPatch_RestFromBase()
        {
            const string patch = """{"temperature":0.7}""";

            var restored = ChatterboxTurboParagraphTtsSettingsDiff.Apply(RecommendedJson, patch);

            Assert.Equal(0.7, restored.Temperature);   // from patch
            Assert.Equal(1.2, restored.RepetitionPenalty); // from base
            Assert.Equal(500, restored.MaxChunkChars);
        }

        [Fact]
        public void RoundTrip_ApplyOfDiff_EqualsEdited()
        {
            var edited = ChatterboxTurboParagraphTtsSettings.Recommended with
            {
                Temperature = 1.0,
                RepetitionPenalty = 1.5,
            };

            var diff = ChatterboxTurboParagraphTtsSettingsDiff.Diff(RecommendedJson, edited);
            var restored = ChatterboxTurboParagraphTtsSettingsDiff.Apply(RecommendedJson, diff);

            Assert.Equal(edited.Temperature, restored.Temperature);
            Assert.Equal(edited.RepetitionPenalty, restored.RepetitionPenalty);
        }
    }
}
