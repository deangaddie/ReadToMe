using System.Collections.Generic;
using Read2Me.Services.Audio.ParagraphTts;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class SentenceSplitterTests
    {
        [Fact]
        public void SingleSentence_ReturnsOneChunkEqualToOriginal()
        {
            var text = "This is a single complete sentence.";

            var chunks = SentenceSplitter.Split(text, minChunkChars: 15);

            Assert.Equal(new[] { text }, chunks);
        }

        [Theory]
        [InlineData("Mr.")]
        [InlineData("Mrs.")]
        [InlineData("Dr.")]
        [InlineData("St.")]
        public void Abbreviation_DoesNotCauseMidPhraseSplit(string title)
        {
            var text = $"I spoke to {title} Smith about the matter yesterday afternoon.";

            var chunks = SentenceSplitter.Split(text, minChunkChars: 15);

            Assert.Equal(new[] { text }, chunks);
        }

        [Fact]
        public void Decimal_DoesNotCauseSplit()
        {
            var text = "The value of pi is 3.14 according to the textbook.";

            var chunks = SentenceSplitter.Split(text, minChunkChars: 15);

            Assert.Equal(new[] { text }, chunks);
        }

        [Fact]
        public void Ellipsis_DoesNotCauseSplit()
        {
            var text = "He paused for a long while... then carried on regardless.";

            var chunks = SentenceSplitter.Split(text, minChunkChars: 15);

            Assert.Equal(new[] { text }, chunks);
        }

        [Fact]
        public void ShortFragment_MergedIntoNeighbour_NotEmittedAlone()
        {
            // "Yes." is below the min-chunk length and must fold into the next sentence.
            var chunks = SentenceSplitter.Split(
                "Yes. I will absolutely be there tomorrow morning without fail.",
                minChunkChars: 15);

            Assert.Equal(new[]
            {
                "Yes. I will absolutely be there tomorrow morning without fail.",
            }, chunks);
        }

        [Fact]
        public void MultiSentenceProse_SplitsIntoOrderedChunks()
        {
            var chunks = SentenceSplitter.Split(
                "The sun rose over the hills. Birds began to sing loudly. A new day had started.",
                minChunkChars: 15);

            Assert.Equal(new[]
            {
                "The sun rose over the hills.",
                "Birds began to sing loudly.",
                "A new day had started.",
            }, chunks);
        }
    }
}
