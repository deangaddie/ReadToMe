using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ChatterboxParagraphTtsSettingsDiffTests
    {
        private static readonly string RecommendedJson =
            JsonSerializer.Serialize(ChatterboxParagraphTtsSettings.Recommended,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        [Fact]
        public void Recommended_HasConfirmedDefaultValues()
        {
            var r = ChatterboxParagraphTtsSettings.Recommended;

            Assert.Equal(0.5, r.Exaggeration);
            Assert.Equal(0.5, r.CfgWeight);
            Assert.Equal(0.8, r.Temperature);
            Assert.Equal(0.05, r.MinP);
            Assert.Equal(1.0, r.TopP);
            Assert.Equal(1.2, r.RepetitionPenalty);
            Assert.Equal(string.Empty, r.BaseUrl);
            Assert.Equal(500, r.MaxChunkChars);
        }

        [Fact]
        public void Diff_UnchangedEdited_ReturnsEmptyObject()
        {
            var diff = ChatterboxParagraphTtsSettingsDiff.Diff(RecommendedJson, ChatterboxParagraphTtsSettings.Recommended);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Empty(obj);
        }

        [Fact]
        public void Diff_OneDifferentField_ReturnsOnlyThatKey()
        {
            var edited = ChatterboxParagraphTtsSettings.Recommended with { Exaggeration = 0.7 };
            var diff = ChatterboxParagraphTtsSettingsDiff.Diff(RecommendedJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.Single(obj);
            Assert.True(obj.ContainsKey("exaggeration"));
            Assert.Equal(0.7, obj["exaggeration"]!.GetValue<double>());
        }

        [Fact]
        public void Diff_NeverIncludesBaseUrl_EvenIfItDiffers()
        {
            var edited = ChatterboxParagraphTtsSettings.Recommended with { BaseUrl = "http://other:9999" };
            var baseJson = JsonSerializer.Serialize(
                ChatterboxParagraphTtsSettings.Recommended with { BaseUrl = "http://original:8000" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var diff = ChatterboxParagraphTtsSettingsDiff.Diff(baseJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.DoesNotContain("baseUrl", obj.Select(kvp => kvp.Key));
        }

        [Fact]
        public void Diff_NeverIncludesMaxChunkChars_EvenIfItDiffers()
        {
            var edited = ChatterboxParagraphTtsSettings.Recommended with { MaxChunkChars = 999 };
            var baseJson = JsonSerializer.Serialize(
                ChatterboxParagraphTtsSettings.Recommended with { MaxChunkChars = 250 },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var diff = ChatterboxParagraphTtsSettingsDiff.Diff(baseJson, edited);

            var obj = JsonNode.Parse(diff)!.AsObject();
            Assert.DoesNotContain("maxChunkChars", obj.Select(kvp => kvp.Key));
        }

        [Fact]
        public void Apply_ReconstructsFullObject_ChangedFromPatch_RestFromBase()
        {
            const string patch = """{"exaggeration":0.7}""";

            var restored = ChatterboxParagraphTtsSettingsDiff.Apply(RecommendedJson, patch);

            Assert.Equal(0.7, restored.Exaggeration);   // from patch
            Assert.Equal(0.5, restored.CfgWeight);       // from base
            Assert.Equal(0.8, restored.Temperature);
            Assert.Equal(0.05, restored.MinP);
            Assert.Equal(1.0, restored.TopP);
            Assert.Equal(1.2, restored.RepetitionPenalty);
            Assert.Equal(500, restored.MaxChunkChars);
        }

        [Fact]
        public void RoundTrip_ApplyOfDiff_EqualsEdited()
        {
            var edited = ChatterboxParagraphTtsSettings.Recommended with
            {
                Exaggeration = 0.7,
                Temperature = 1.0,
                RepetitionPenalty = 1.5,
            };

            var diff = ChatterboxParagraphTtsSettingsDiff.Diff(RecommendedJson, edited);
            var restored = ChatterboxParagraphTtsSettingsDiff.Apply(RecommendedJson, diff);

            Assert.Equal(edited.Exaggeration, restored.Exaggeration);
            Assert.Equal(edited.CfgWeight, restored.CfgWeight);
            Assert.Equal(edited.Temperature, restored.Temperature);
            Assert.Equal(edited.MinP, restored.MinP);
            Assert.Equal(edited.TopP, restored.TopP);
            Assert.Equal(edited.RepetitionPenalty, restored.RepetitionPenalty);
        }
    }
}
