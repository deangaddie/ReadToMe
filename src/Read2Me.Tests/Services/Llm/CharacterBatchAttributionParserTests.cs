using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class CharacterBatchAttributionParserTests
    {
        [Fact]
        public void PlainJsonArray_Parses()
        {
            var raw = """[ { "index": 0, "character": "Alice", "voice_instructions": "calm" }, { "index": 1, "character": "Bob" } ]""";

            Assert.True(CharacterBatchAttributionParser.TryParse(raw, out var results));
            Assert.Equal(2, results.Count);
            Assert.Equal("Alice", results[0].Character);
            Assert.Equal("calm", results[0].VoiceInstructions);
            Assert.Equal("Bob", results[1].Character);
            Assert.Equal("", results[1].VoiceInstructions);
        }

        [Fact]
        public void EntriesWithReasoning_ParseAndCaptureReasoning()
        {
            var raw = """[ { "index": 0, "reasoning": "tag after quote", "character": "Alice", "voice_instructions": "calm" } ]""";

            Assert.True(CharacterBatchAttributionParser.TryParse(raw, out var results));
            Assert.Equal("Alice", results[0].Character);
            Assert.Equal("tag after quote", results[0].Reasoning);
        }

        [Fact]
        public void CodeFencedArray_Parses()
        {
            var raw = """
                ```json
                [ { "index": 0, "character": "Alice" } ]
                ```
                """;

            Assert.True(CharacterBatchAttributionParser.TryParse(raw, out var results));
            Assert.Equal("Alice", results[0].Character);
        }

        [Fact]
        public void ProseAroundArray_Parses()
        {
            var raw = """Here are the speakers: [ { "index": 0, "character": "Alice" } ] Hope that helps!""";

            Assert.True(CharacterBatchAttributionParser.TryParse(raw, out var results));
            Assert.Equal("Alice", results[0].Character);
        }

        [Fact]
        public void MissingIndexEntry_IsDropped_OthersKept()
        {
            var raw = """[ { "character": "NoIndex" }, { "index": 1, "character": "Bob" } ]""";

            Assert.True(CharacterBatchAttributionParser.TryParse(raw, out var results));
            var entry = Assert.Single(results);
            Assert.Equal(1, entry.Key);
            Assert.Equal("Bob", entry.Value.Character);
        }

        [Fact]
        public void BlankCharacterEntry_IsDropped()
        {
            var raw = """[ { "index": 0, "character": "  " }, { "index": 1, "character": "Bob" } ]""";

            Assert.True(CharacterBatchAttributionParser.TryParse(raw, out var results));
            Assert.Single(results);
            Assert.False(results.ContainsKey(0));
        }

        [Fact]
        public void DuplicateIndex_FirstEntryWins()
        {
            var raw = """[ { "index": 0, "character": "First" }, { "index": 0, "character": "Second" } ]""";

            Assert.True(CharacterBatchAttributionParser.TryParse(raw, out var results));
            Assert.Equal("First", results[0].Character);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("no array here")]
        [InlineData("[ not valid json ]")]
        [InlineData("{ \"index\": 0, \"character\": \"Alice\" }")]
        public void Garbage_ReturnsFalse(string raw)
        {
            Assert.False(CharacterBatchAttributionParser.TryParse(raw, out _));
        }

        [Fact]
        public void EmptyArray_ParsesToEmptyMap()
        {
            Assert.True(CharacterBatchAttributionParser.TryParse("[]", out var results));
            Assert.Empty(results);
        }
    }
}
