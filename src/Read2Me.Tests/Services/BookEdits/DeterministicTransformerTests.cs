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
        [InlineData("Part one", null, CaseMode.Upper, "PART ONE")]
        [InlineData("PART One", null, CaseMode.Lower, "part one")]
        [InlineData("Hari Seldon said", "Seldon", CaseMode.Upper, "Hari SELDON said")]
        [InlineData("THE MULE and THE GENERAL", @"\bTHE\b", CaseMode.Lower, "the MULE and the GENERAL")]
        [InlineData("ALREADY UPPER", null, CaseMode.Upper, "ALREADY UPPER")]
        [InlineData("no match here", "xyz", CaseMode.Upper, "no match here")]
        [InlineData("élan vital", null, CaseMode.Upper, "ÉLAN VITAL")]
        public void ChangeCase_UpperLower_ReCasesWholeValueOrMatchedSpans(
            string value, string? pattern, CaseMode mode, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, pattern, mode));
        }

        [Theory]
        [InlineData(CaseMode.Sentence)]
        [InlineData(CaseMode.Title)]
        public void ChangeCase_SentenceAndTitle_NotSupportedYet(CaseMode mode)
        {
            Assert.Throws<NotSupportedException>(() => DeterministicTransformer.ChangeCase("SHOUT", null, mode));
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
