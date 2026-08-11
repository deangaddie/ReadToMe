using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class ItemAttributionParserTests
    {
        [Fact]
        public void PlainJsonObject_Parses()
        {
            var raw = """
                {
                  "reasoning": "tag after quote",
                  "items": [
                    { "index": 0, "speaker": "Alice", "voice_instructions": "warm" },
                    { "index": 2, "speaker": "unknown", "voice_instructions": "" }
                  ]
                }
                """;

            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Equal("tag after quote", result.Reasoning);
            Assert.Equal(2, result.Items.Count);
            Assert.Equal(0, result.Items[0].Index);
            Assert.Equal("Alice", result.Items[0].Speaker);
            Assert.Equal("warm", result.Items[0].VoiceInstructions);
            Assert.Equal(2, result.Items[1].Index);
            Assert.Equal("unknown", result.Items[1].Speaker);
        }

        [Fact]
        public void CodeFencedObject_Parses()
        {
            var raw = """
                ```json
                { "reasoning": "r", "items": [ { "index": 1, "speaker": "Alice", "voice_instructions": "" } ] }
                ```
                """;

            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", Assert.Single(result.Items).Speaker);
        }

        [Fact]
        public void ProseAroundObject_Parses()
        {
            var raw = """Here you go: { "reasoning": "r", "items": [ { "index": 1, "speaker": "Alice", "voice_instructions": "" } ] } Hope that helps!""";

            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", Assert.Single(result.Items).Speaker);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("no object here")]
        [InlineData("{ not valid json }")]
        public void Garbage_ReturnsFalse(string raw)
        {
            Assert.False(ItemAttributionParser.TryParse(raw, out _));
        }

        [Theory]
        [InlineData("""{ "reasoning": "r", "items": [] }""")]
        [InlineData("""{ "reasoning": "r" }""")]
        public void NoItems_ParsesAsZeroAttributions(string raw)
        {
            // Answering nothing is a valid answer: the paragraph's dialog items stay unattributed
            // and escalate as Unknown — it is not a ParseFailure.
            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Empty(result.Items);
        }

        [Theory]
        [InlineData("""{ "items": [ { "speaker": "Alice", "voice_instructions": "" } ] }""")]
        [InlineData("""{ "items": [ { "index": "first", "speaker": "Alice", "voice_instructions": "" } ] }""")]
        [InlineData("""{ "items": [ { "index": 1.5, "speaker": "Alice", "voice_instructions": "" } ] }""")]
        [InlineData("""{ "items": [ { "index": null, "speaker": "Alice", "voice_instructions": "" } ] }""")]
        public void ItemWithoutUsableIndex_IsDropped(string raw)
        {
            // Item-level tolerance: one unusable entry must not cost the whole paragraph's answer.
            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Empty(result.Items);
        }

        [Theory]
        [InlineData("""{ "items": [ { "index": 0, "voice_instructions": "" } ] }""")]
        [InlineData("""{ "items": [ { "index": 0, "speaker": "", "voice_instructions": "" } ] }""")]
        [InlineData("""{ "items": [ { "index": 0, "speaker": "   ", "voice_instructions": "" } ] }""")]
        [InlineData("""{ "items": [ { "index": 0, "speaker": null, "voice_instructions": "" } ] }""")]
        public void ItemWithoutSpeaker_IsDropped(string raw)
        {
            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Empty(result.Items);
        }

        [Fact]
        public void UsableItems_SurviveAlongsideDroppedOnes()
        {
            var raw = """
                { "reasoning": "r", "items": [
                  { "index": "nope", "speaker": "Ghost", "voice_instructions": "" },
                  { "index": 3, "speaker": "Alice", "voice_instructions": "warm" }
                ] }
                """;

            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            var item = Assert.Single(result.Items);
            Assert.Equal(3, item.Index);
            Assert.Equal("Alice", item.Speaker);
        }

        [Fact]
        public void DuplicateItemIndex_FirstWins()
        {
            var raw = """
                { "reasoning": "r", "items": [
                  { "index": 1, "speaker": "Alice", "voice_instructions": "" },
                  { "index": 1, "speaker": "Bob", "voice_instructions": "" }
                ] }
                """;

            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", Assert.Single(result.Items).Speaker);
        }

        [Fact]
        public void Speaker_IsTrimmed()
        {
            var raw = """{ "items": [ { "index": 0, "speaker": "  Alice  ", "voice_instructions": "" } ] }""";

            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", Assert.Single(result.Items).Speaker);
        }

        [Theory]
        [InlineData("""{ "items": [ { "index": 0, "speaker": "Alice" } ] }""")]
        [InlineData("""{ "items": [ { "index": 0, "speaker": "Alice", "voice_instructions": null } ] }""")]
        public void MissingVoiceInstructions_StayNull(string raw)
        {
            // The answer is the whole truth for an item it names: no instructions means clear them,
            // so null must survive the parser to reach the apply (spec §1).
            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Null(Assert.Single(result.Items).VoiceInstructions);
        }

        [Fact]
        public void LiteralUnicodeEscapes_InSpeaker_AreUnescaped()
        {
            // Double-escaped on the wire: JSON parse leaves a literal ’ in the string.
            var raw = """{ "items": [ { "index": 0, "speaker": "O\\u2019Brien", "voice_instructions": "" } ] }""";

            Assert.True(ItemAttributionParser.TryParse(raw, out var result));
            Assert.Equal("O’Brien", Assert.Single(result.Items).Speaker);
        }

        [Fact]
        public void Batch_ValidEntries_ParseByParagraphIndex()
        {
            var raw = """
                [
                  { "index": 0, "reasoning": "a", "items": [ { "index": 1, "speaker": "Alice", "voice_instructions": "" } ] },
                  { "index": 1, "reasoning": "b", "items": [ { "index": 0, "speaker": "Bob", "voice_instructions": "cold" } ] }
                ]
                """;

            Assert.True(ItemAttributionParser.TryParseBatch(raw, [0, 1], out var results));
            Assert.Equal(2, results.Count);
            Assert.Equal("Alice", results[0].Items[0].Speaker);
            Assert.Equal("b", results[1].Reasoning);
            Assert.Equal("cold", results[1].Items[0].VoiceInstructions);
        }

        [Fact]
        public void Batch_EntryWithNoItems_IsAnsweredWithZeroAttributions()
        {
            var raw = """[ { "index": 0, "reasoning": "a", "items": [] } ]""";

            Assert.True(ItemAttributionParser.TryParseBatch(raw, [0], out var results));
            Assert.Empty(Assert.Single(results).Value.Items);
        }

        [Fact]
        public void Batch_DroppedItems_DoNotFailTheEntry()
        {
            var raw = """
                [ { "index": 0, "reasoning": "a", "items": [
                    { "index": 0, "speaker": "", "voice_instructions": "" },
                    { "index": 1, "speaker": "Alice", "voice_instructions": "" } ] } ]
                """;

            Assert.True(ItemAttributionParser.TryParseBatch(raw, [0], out var results));
            Assert.Equal(1, Assert.Single(Assert.Single(results).Value.Items).Index);
        }

        [Fact]
        public void Batch_DuplicateParagraphIndex_FirstEntryWins()
        {
            var raw = """
                [
                  { "index": 0, "reasoning": "a", "items": [ { "index": 0, "speaker": "Alice", "voice_instructions": "" } ] },
                  { "index": 0, "reasoning": "b", "items": [ { "index": 0, "speaker": "Bob", "voice_instructions": "" } ] }
                ]
                """;

            Assert.True(ItemAttributionParser.TryParseBatch(raw, [0], out var results));
            Assert.Equal("Alice", results[0].Items[0].Speaker);
        }

        [Fact]
        public void Batch_ExtraUnrequestedParagraphIndex_Ignored()
        {
            // Trial models answer for context paragraphs too — extra entries must not fail the parse.
            var raw = """
                [
                  { "index": 0, "reasoning": "a", "items": [ { "index": 0, "speaker": "Alice", "voice_instructions": "" } ] },
                  { "index": 7, "reasoning": "ctx", "items": [ { "index": 0, "speaker": "Bob", "voice_instructions": "" } ] }
                ]
                """;

            Assert.True(ItemAttributionParser.TryParseBatch(raw, [0], out var results));
            Assert.Equal(0, Assert.Single(results).Key);
        }

        [Fact]
        public void Batch_MissingRequestedParagraphIndex_Fails()
        {
            // Chunk-level all-or-nothing is unchanged: escalation needs every requested paragraph.
            var raw = """
                [ { "index": 0, "reasoning": "a", "items": [ { "index": 0, "speaker": "Alice", "voice_instructions": "" } ] } ]
                """;

            Assert.False(ItemAttributionParser.TryParseBatch(raw, [0, 1], out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("no array here")]
        [InlineData("[ not valid json ]")]
        [InlineData("""{ "index": 0, "items": [] }""")]
        public void Batch_Garbage_ReturnsFalse(string raw)
        {
            Assert.False(ItemAttributionParser.TryParseBatch(raw, [0], out _));
        }
    }
}
