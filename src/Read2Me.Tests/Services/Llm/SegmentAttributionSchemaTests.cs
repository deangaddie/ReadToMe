using System.Text.Json.Nodes;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class SegmentAttributionSchemaTests
    {
        // Spec JSON verbatim from .scratch/multi-character-paragraphs/spec.md § llama.cpp json_schema.
        private const string SpecSingleSchema = """
            {
              "type": "object",
              "properties": {
                "reasoning": { "type": "string" },
                "segments": {
                  "type": "array",
                  "minItems": 1,
                  "items": {
                    "type": "object",
                    "properties": {
                      "text": { "type": "string" },
                      "type": { "type": "string", "enum": ["narration", "dialog"] },
                      "speaker": { "type": "string" },
                      "voice_instructions": { "type": "string" }
                    },
                    "required": ["text", "type", "speaker", "voice_instructions"]
                  }
                }
              },
              "required": ["reasoning", "segments"]
            }
            """;

        [Fact]
        public void SingleSchema_MatchesSpec()
        {
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(SpecSingleSchema),
                JsonNode.Parse(SegmentAttributionSchema.JsonSchema)));
        }

        [Fact]
        public void SingleSchema_PropertyOrder_ReasoningBeforeSegments_TextBeforeSpeaker()
        {
            // Property order is deliberate: the model reasons before answering, commits text before
            // attributing. DeepEquals ignores order, so assert it on the raw string.
            var schema = SegmentAttributionSchema.JsonSchema;
            Assert.True(schema.IndexOf("\"reasoning\"", StringComparison.Ordinal)
                < schema.IndexOf("\"segments\"", StringComparison.Ordinal));
            Assert.True(schema.IndexOf("\"text\"", StringComparison.Ordinal)
                < schema.IndexOf("\"speaker\"", StringComparison.Ordinal));
        }

        [Fact]
        public void BatchSchema_WrapsSingleInArrayWithIndex()
        {
            var batch = JsonNode.Parse(SegmentBatchAttributionSchema.JsonSchema)!.AsObject();
            Assert.Equal("array", (string?)batch["type"]);

            var entry = batch["items"]!.AsObject();
            Assert.Equal("integer", (string?)entry["properties"]!["index"]!["type"]);
            Assert.Equal(
                new[] { "index", "reasoning", "segments" },
                entry["required"]!.AsArray().Select(n => (string?)n));

            // The segments property must be exactly the spec's single-response segments schema.
            var specSegments = JsonNode.Parse(SpecSingleSchema)!["properties"]!["segments"];
            Assert.True(JsonNode.DeepEquals(specSegments, entry["properties"]!["segments"]));
        }

        [Fact]
        public void JsonExamples_ParseAgainstOwnParser()
        {
            Assert.True(SegmentAttributionParser.TryParse(
                SegmentAttributionSchema.JsonExample, out _));
            Assert.True(SegmentAttributionParser.TryParseBatch(
                SegmentBatchAttributionSchema.JsonExample, [0], out _));
        }
    }
}
