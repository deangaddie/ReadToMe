using System.Text.Json.Nodes;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class ItemAttributionSchemaTests
    {
        // Spec JSON verbatim from .scratch/frozen-item-boundaries/spec.md §1 — the answer names an
        // existing item by index; it never restates text and never answers a type.
        private const string SpecSingleSchema = """
            {
              "type": "object",
              "properties": {
                "reasoning": { "type": "string" },
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "index": { "type": "integer" },
                      "speaker": { "type": "string" },
                      "voice_instructions": { "type": "string" }
                    },
                    "required": ["index", "speaker", "voice_instructions"]
                  }
                }
              },
              "required": ["reasoning", "items"]
            }
            """;

        [Fact]
        public void SingleSchema_MatchesSpec()
        {
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(SpecSingleSchema),
                JsonNode.Parse(ItemAttributionSchema.JsonSchema)));
        }

        [Fact]
        public void SingleSchema_HasNoMinItems()
        {
            // An empty items list is a valid answer (nothing attributable), so generation must not
            // be forced to invent one.
            Assert.DoesNotContain("minItems", ItemAttributionSchema.JsonSchema, StringComparison.Ordinal);
            Assert.DoesNotContain("minItems", ItemBatchAttributionSchema.JsonSchema, StringComparison.Ordinal);
        }

        [Fact]
        public void SingleSchema_PropertyOrder_ReasoningBeforeItems_IndexBeforeSpeaker()
        {
            // Property order is deliberate: the model reasons before answering, and names the item
            // before naming its speaker. DeepEquals ignores order, so assert it on the raw string.
            var schema = ItemAttributionSchema.JsonSchema;
            Assert.True(schema.IndexOf("\"reasoning\"", StringComparison.Ordinal)
                < schema.IndexOf("\"items\"", StringComparison.Ordinal));
            Assert.True(schema.IndexOf("\"index\"", StringComparison.Ordinal)
                < schema.IndexOf("\"speaker\"", StringComparison.Ordinal));
        }

        [Fact]
        public void BatchSchema_WrapsSingleInArrayWithParagraphIndex()
        {
            var batch = JsonNode.Parse(ItemBatchAttributionSchema.JsonSchema)!.AsObject();
            Assert.Equal("array", (string?)batch["type"]);

            var entry = batch["items"]!.AsObject();
            Assert.Equal("integer", (string?)entry["properties"]!["index"]!["type"]);
            Assert.Equal(
                new[] { "index", "reasoning", "items" },
                entry["required"]!.AsArray().Select(n => (string?)n));

            // The items property must be exactly the spec's single-response items schema.
            var specItems = JsonNode.Parse(SpecSingleSchema)!["properties"]!["items"];
            Assert.True(JsonNode.DeepEquals(specItems, entry["properties"]!["items"]));
        }

        [Fact]
        public void JsonExamples_ParseAgainstOwnParser()
        {
            Assert.True(ItemAttributionParser.TryParse(
                ItemAttributionSchema.JsonExample, out _));
            Assert.True(ItemAttributionParser.TryParseBatch(
                ItemBatchAttributionSchema.JsonExample, [0], out _));
        }
    }
}
