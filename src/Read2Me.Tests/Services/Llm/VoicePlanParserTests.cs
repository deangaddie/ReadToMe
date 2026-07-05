using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class VoicePlanParserTests
    {
        [Fact]
        public void TryParse_PlainArray_ReturnsVoices()
        {
            var raw = """
                [
                  { "name": "Young Pip", "description": "Part 1, Chapter 1 to Part 1, Chapter 7", "design_prompt": "a boy's voice" },
                  { "name": "Adult Pip", "description": "Part 2 onwards", "design_prompt": "a grown man's voice" }
                ]
                """;

            Assert.True(VoicePlanParser.TryParse(raw, out var voices));
            Assert.Equal(2, voices.Count);
            Assert.Equal("Young Pip", voices[0].Name);
            Assert.Equal("Part 1, Chapter 1 to Part 1, Chapter 7", voices[0].Description);
            Assert.Equal("a boy's voice", voices[0].DesignPrompt);
            Assert.Equal("Adult Pip", voices[1].Name);
        }

        [Fact]
        public void TryParse_CodeFencedArray_ReturnsVoices()
        {
            var raw = "```json\n[ { \"name\": \"Default\", \"description\": \"whole book\", \"design_prompt\": \"warm voice\" } ]\n```";

            Assert.True(VoicePlanParser.TryParse(raw, out var voices));
            Assert.Single(voices);
            Assert.Equal("Default", voices[0].Name);
        }

        [Fact]
        public void TryParse_LeadingAndTrailingProse_ReturnsVoices()
        {
            var raw = "Here are the voices:\n[ { \"name\": \"Default\", \"description\": \"whole book\", \"design_prompt\": \"warm voice\" } ]\nHope this helps!";

            Assert.True(VoicePlanParser.TryParse(raw, out var voices));
            Assert.Single(voices);
        }

        [Fact]
        public void TryParse_MissingDescription_ReturnsNullDescription()
        {
            var raw = "[ { \"name\": \"Default\", \"design_prompt\": \"warm voice\" } ]";

            Assert.True(VoicePlanParser.TryParse(raw, out var voices));
            Assert.Single(voices);
            Assert.Null(voices[0].Description);
        }

        [Fact]
        public void TryParse_EntriesWithoutNameOrPrompt_Skipped()
        {
            var raw = """
                [
                  { "name": "", "description": "x", "design_prompt": "y" },
                  { "name": "Valid", "description": "x", "design_prompt": "" },
                  { "name": "Kept", "description": "x", "design_prompt": "voice" }
                ]
                """;

            Assert.True(VoicePlanParser.TryParse(raw, out var voices));
            Assert.Single(voices);
            Assert.Equal("Kept", voices[0].Name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("no json here")]
        [InlineData("[]")]
        [InlineData("[ { \"name\": \"\", \"design_prompt\": \"\" } ]")]
        [InlineData("[ not valid json ]")]
        public void TryParse_InvalidOrEmpty_ReturnsFalse(string raw)
        {
            Assert.False(VoicePlanParser.TryParse(raw, out _));
        }
    }
}
