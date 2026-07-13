using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class SegmentAttributionParserTests
    {
        [Fact]
        public void CodeFencedObject_Parses()
        {
            var raw = """
                ```json
                { "reasoning": "r", "segments": [ { "text": "Hi.", "type": "dialog", "speaker": "Alice", "voice_instructions": "" } ] }
                ```
                """;

            Assert.True(SegmentAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", Assert.Single(result.Segments).Speaker);
        }

        [Fact]
        public void ProseAroundObject_Parses()
        {
            var raw = """Here you go: { "reasoning": "r", "segments": [ { "text": "Hi.", "type": "dialog", "speaker": "Alice", "voice_instructions": "" } ] } Hope that helps!""";

            Assert.True(SegmentAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", Assert.Single(result.Segments).Speaker);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("no object here")]
        [InlineData("{ not valid json }")]
        [InlineData("""{ "reasoning": "r", "segments": [] }""")]
        [InlineData("""{ "reasoning": "r" }""")]
        [InlineData("""{ "reasoning": "r", "segments": [ { "type": "dialog", "speaker": "A", "voice_instructions": "" } ] }""")]
        [InlineData("""{ "reasoning": "r", "segments": [ { "text": "Hi.", "type": "shouting", "speaker": "A", "voice_instructions": "" } ] }""")]
        public void Garbage_ReturnsFalse(string raw)
        {
            Assert.False(SegmentAttributionParser.TryParse(raw, out _));
        }

        [Theory]
        [InlineData("""{ "reasoning": "r", "segments": [ { "text": "Hi.", "type": "dialog", "voice_instructions": "" } ] }""")]
        [InlineData("""{ "reasoning": "r", "segments": [ { "text": "Hi.", "type": "dialog", "speaker": "  ", "voice_instructions": "" } ] }""")]
        public void DialogSegment_MissingSpeaker_FailsParse(string raw)
        {
            // Schema requires speaker on every segment — a blank one is a contract violation
            // (ParseFailure tier), not a silent "unknown" repair.
            Assert.False(SegmentAttributionParser.TryParse(raw, out _));
        }

        [Fact]
        public void NarrationSegment_VoiceInstructions_CoercedToEmpty()
        {
            var raw = """
                { "reasoning": "r", "segments": [ { "text": "she said.", "type": "narration", "speaker": "narrator", "voice_instructions": "softly" } ] }
                """;

            Assert.True(SegmentAttributionParser.TryParse(raw, out var result));
            Assert.Equal("", Assert.Single(result.Segments).VoiceInstructions);
        }

        [Fact]
        public void Batch_DuplicateIndex_FirstEntryWins()
        {
            var raw = """
                [
                  { "index": 0, "reasoning": "a", "segments": [ { "text": "First.", "type": "narration", "speaker": "narrator", "voice_instructions": "" } ] },
                  { "index": 0, "reasoning": "b", "segments": [ { "text": "Second.", "type": "narration", "speaker": "narrator", "voice_instructions": "" } ] }
                ]
                """;

            Assert.True(SegmentAttributionParser.TryParseBatch(raw, [0], out var results));
            Assert.Equal("First.", results[0].Segments[0].Text);
        }

        [Theory]
        [InlineData("")]
        [InlineData("no array here")]
        [InlineData("[ not valid json ]")]
        [InlineData("""{ "index": 0, "segments": [] }""")]
        public void Batch_Garbage_ReturnsFalse(string raw)
        {
            Assert.False(SegmentAttributionParser.TryParseBatch(raw, [0], out _));
        }

        [Fact]
        public void Batch_RequestedEntryWithUnusableSegments_Fails()
        {
            var raw = """
                [ { "index": 0, "reasoning": "a", "segments": [] } ]
                """;

            Assert.False(SegmentAttributionParser.TryParseBatch(raw, [0], out _));
        }

        [Fact]
        public void Batch_ValidEntries_ParseByIndex()
        {
            var raw = """
                [
                  { "index": 0, "reasoning": "a", "segments": [ { "text": "Hi.", "type": "dialog", "speaker": "Alice", "voice_instructions": "" } ] },
                  { "index": 1, "reasoning": "b", "segments": [ { "text": "Prose.", "type": "narration", "speaker": "narrator", "voice_instructions": "" } ] }
                ]
                """;

            Assert.True(SegmentAttributionParser.TryParseBatch(raw, [0, 1], out var results));
            Assert.Equal(2, results.Count);
            Assert.Equal("Alice", results[0].Segments[0].Speaker);
            Assert.Equal(AttributionSegmentType.Narration, results[1].Segments[0].Type);
        }

        [Fact]
        public void Batch_MissingRequestedIndex_Fails()
        {
            var raw = """
                [ { "index": 0, "reasoning": "a", "segments": [ { "text": "Hi.", "type": "dialog", "speaker": "Alice", "voice_instructions": "" } ] } ]
                """;

            Assert.False(SegmentAttributionParser.TryParseBatch(raw, [0, 1], out _));
        }

        [Fact]
        public void Batch_ExtraUnrequestedIndex_Ignored()
        {
            // Trial models answer for context paragraphs too — extra entries must not fail the parse.
            var raw = """
                [
                  { "index": 0, "reasoning": "a", "segments": [ { "text": "Hi.", "type": "dialog", "speaker": "Alice", "voice_instructions": "" } ] },
                  { "index": 7, "reasoning": "ctx", "segments": [ { "text": "Prose.", "type": "narration", "speaker": "narrator", "voice_instructions": "" } ] }
                ]
                """;

            Assert.True(SegmentAttributionParser.TryParseBatch(raw, [0], out var results));
            var entry = Assert.Single(results);
            Assert.Equal(0, entry.Key);
        }

        [Fact]
        public void NarrationSegment_SpeakerCoercedToNarrator()
        {
            var raw = """
                { "reasoning": "r", "segments": [
                  { "text": "she said.", "type": "narration", "speaker": "Alice", "voice_instructions": "" }
                ] }
                """;

            Assert.True(SegmentAttributionParser.TryParse(raw, out var result));
            Assert.Equal("narrator", Assert.Single(result.Segments).Speaker);
        }

        [Fact]
        public void LiteralUnicodeEscapes_InSegmentText_AreUnescaped()
        {
            // Double-escaped on the wire: JSON parse leaves a literal ’ in the string.
            var raw = """
                { "reasoning": "r", "segments": [
                  { "text": "Alice\\u2019s cat \\uD83D\\uDE00", "type": "narration", "speaker": "narrator", "voice_instructions": "" }
                ] }
                """;

            Assert.True(SegmentAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice’s cat \U0001F600", Assert.Single(result.Segments).Text);
        }

        [Fact]
        public void PlainJsonObject_Parses()
        {
            var raw = """
                {
                  "reasoning": "tag after quote",
                  "segments": [
                    { "text": "\"Hello,\" ", "type": "dialog", "speaker": "Alice", "voice_instructions": "warm" },
                    { "text": "she said.", "type": "narration", "speaker": "narrator", "voice_instructions": "" }
                  ]
                }
                """;

            Assert.True(SegmentAttributionParser.TryParse(raw, out var result));
            Assert.Equal("tag after quote", result.Reasoning);
            Assert.Equal(2, result.Segments.Count);
            Assert.Equal("\"Hello,\" ", result.Segments[0].Text);
            Assert.Equal(AttributionSegmentType.Dialog, result.Segments[0].Type);
            Assert.Equal("Alice", result.Segments[0].Speaker);
            Assert.Equal("warm", result.Segments[0].VoiceInstructions);
            Assert.Equal(AttributionSegmentType.Narration, result.Segments[1].Type);
            Assert.Equal("narrator", result.Segments[1].Speaker);
        }
    }
}
