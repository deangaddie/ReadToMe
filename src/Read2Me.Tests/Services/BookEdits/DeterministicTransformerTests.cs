using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class DeterministicTransformerTests
    {
        [Theory]
        [InlineData("Chapter I. Intro", @"^Chapter [IVXLC]+\.\s*", "", "Intro")]
        [InlineData("Hello world", "world", "there", "Hello there")]
        [InlineData("abc123def", @"(\d+)", "[$1]", "abc[123]def")]
        [InlineData("no match", "xyz", "q", "no match")]
        public void RegexReplace_AppliesPattern(string old, string pattern, string replacement, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.RegexReplace(old, pattern, replacement));
        }

        [Fact]
        public void RegexReplace_NullReplacement_RemovesMatch()
        {
            Assert.Equal("Intro", DeterministicTransformer.RegexReplace("1. Intro", @"^\d+\.\s*", null));
        }

        [Theory]
        [InlineData("Chapter {n}: {old}", 3, "The Storm", "Chapter 3: The Storm")]
        [InlineData("{old}!", 1, "Hello", "Hello!")]
        [InlineData("Part {n}", 12, "ignored", "Part 12")]
        [InlineData("No tokens", 5, "x", "No tokens")]
        public void RenderTemplate_SubstitutesTokens(string template, int n, string old, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.RenderTemplate(template, n, old));
        }
    }
}
