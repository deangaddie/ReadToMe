using Read2Me.Services.Audio.ParagraphTts;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class SentenceChunkerTests
    {
        [Fact]
        public void EmptyInput_ReturnsEmpty()
        {
            var result = SentenceChunker.Chunk([], maxChunkChars: 500);

            Assert.Empty(result);
        }

        [Fact]
        public void SingleSentence_ReturnsThatSentence()
        {
            var sentence = "This is one sentence.";

            var result = SentenceChunker.Chunk([sentence], maxChunkChars: 500);

            Assert.Equal(new[] { sentence }, result);
        }

        [Fact]
        public void AllFitUnderCap_ReturnsOneSpaceJoinedChunk()
        {
            var sentences = new[] { "Hello world.", "How are you?", "Fine thanks." };

            var result = SentenceChunker.Chunk(sentences, maxChunkChars: 500);

            Assert.Equal(new[] { "Hello world. How are you? Fine thanks." }, result);
        }

        [Fact]
        public void OverCap_PacksGreedily_FewestChunks()
        {
            // cap=30, threshold=15
            // "Hello world." (12) + " " + "How are you?" (12) = 25 ≤ 30 → chunk1
            // "I am doing well today." (22) → 25+1+22=48 > 30 → emit chunk1, chunk2="I am doing well today."
            // Orphan check: chunk2 len=22 >= threshold=15 → no merge
            // Result: ["Hello world. How are you?", "I am doing well today."]
            var sentences = new[] { "Hello world.", "How are you?", "I am doing well today." };

            var result = SentenceChunker.Chunk(sentences, maxChunkChars: 30);

            Assert.Equal(2, result.Count);
            Assert.Equal("Hello world. How are you?", result[0]);
            Assert.Equal("I am doing well today.", result[1]);
        }

        [Fact]
        public void OversizedSingleSentence_EmittedAsOwnChunk()
        {
            // Sentence longer than cap must not be split mid-sentence
            var longSentence = "This sentence is definitely longer than twenty characters.";
            var sentences = new[] { longSentence };

            var result = SentenceChunker.Chunk(sentences, maxChunkChars: 20);

            Assert.Equal(new[] { longSentence }, result);
        }

        [Fact]
        public void TinyFinalChunk_MergedBackIntoPrevious()
        {
            // cap=50, threshold=25
            // "Alpha sentence here." (20) + " " + "Beta sentence here." (19) = 40 ≤ 50 → one chunk
            // "Hi." (3) → 40+1+3=44 ≤ 50 → still fits → need different data
            // Use cap=30, threshold=15
            // "Hello world." (12) + " " + "How are you?" (12) = 25 ≤ 30 → chunk1
            // "Ok." (3) → 25+1+3=29 ≤ 30 → still fits in chunk1 → need bigger sentences
            //
            // cap=25, threshold=12
            // "Hello world." (12) fits alone → chunk1="Hello world."
            // "How are you?" (12) → 12+1+12=25 ≤ 25 → chunk1="Hello world. How are you?"
            // "Ok." (3) → 25+1+3=29 > 25 → emit chunk1, chunk2="Ok."
            // Orphan check: "Ok." len=3 < threshold=12 → merge back
            // Final: ["Hello world. How are you? Ok."] — merged chunk exceeds cap (29)
            var sentences = new[] { "Hello world.", "How are you?", "Ok." };

            var result = SentenceChunker.Chunk(sentences, maxChunkChars: 25);

            Assert.Single(result);
            Assert.Equal("Hello world. How are you? Ok.", result[0]);
        }

        [Theory]
        [InlineData(500, 250)]
        [InlineData(200, 100)]
        public void OrphanThresholdScalesWithCap(int cap, int threshold)
        {
            // Build a case where the last chunk is exactly at threshold-1 (orphan)
            // and verify it merges. Use sentences that force exactly 2 chunks.
            //
            // We need: chunk1 fills near cap, chunk2.Length < threshold
            // chunk2 = string of length (threshold - 1)
            var shortOrphan = new string('x', threshold - 1);
            // chunk1: one sentence that fills cap exactly (or near)
            var bigSentence = new string('a', cap);

            var sentences = new[] { bigSentence, shortOrphan };

            var result = SentenceChunker.Chunk(sentences, maxChunkChars: cap);

            // bigSentence alone exceeds cap (it equals cap exactly, so it is oversized
            // when combined but as sole sentence it IS the chunk). shortOrphan < threshold → merge.
            Assert.Single(result);
            Assert.Equal(bigSentence + " " + shortOrphan, result[0]);
        }

        [Fact]
        public void SingleChunk_NoMergeBack()
        {
            // All sentences fit in one chunk → nothing to merge into → still one chunk
            var sentences = new[] { "Short.", "Also short." };

            var result = SentenceChunker.Chunk(sentences, maxChunkChars: 500);

            Assert.Single(result);
            Assert.Equal("Short. Also short.", result[0]);
        }
    }
}
