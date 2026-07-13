using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class SegmentTextNormalizerTests
    {
        [Fact]
        public void WhitespaceRuns_CollapseToSingleSpace()
        {
            Assert.Equal("a b c", SegmentTextNormalizer.Normalize("a  b\t\n c"));
        }

        [Fact]
        public void LeadingTrailingWhitespace_Trimmed()
        {
            Assert.Equal("hello", SegmentTextNormalizer.Normalize("  hello \n"));
        }

        [Fact]
        public void CurlyQuotes_FoldToStraight()
        {
            Assert.Equal("\"No,\" she said. 'Yes' isn't", SegmentTextNormalizer.Normalize("“No,” she said. ‘Yes’ isn’t"));
        }

        [Theory]
        [InlineData("first—second")]   // em-dash
        [InlineData("first–second")]   // en-dash
        [InlineData("first--second")]       // hyphen run
        [InlineData("first---second")]
        public void DashClass_FoldsToSingleHyphen(string input)
        {
            Assert.Equal("first-second", SegmentTextNormalizer.Normalize(input));
        }

        [Fact]
        public void SingleHyphen_Unchanged()
        {
            Assert.Equal("well-known", SegmentTextNormalizer.Normalize("well-known"));
        }

        [Fact]
        public void EquivalentTexts_NormalizeIdentically()
        {
            var original = "“Sentence first—verdict afterwards.”";
            var llm = "\"Sentence  first--verdict afterwards.\"";
            Assert.Equal(SegmentTextNormalizer.Normalize(original), SegmentTextNormalizer.Normalize(llm));
        }

        [Fact]
        public void CaseAndLetters_Preserved()
        {
            Assert.Equal("The Queen", SegmentTextNormalizer.Normalize("The Queen"));
        }
    }
}
