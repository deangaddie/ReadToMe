using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class SegmentAlignerTests
    {
        private static AttributionSegment Dialog(string text, string speaker = "Alice", string voice = "") =>
            new(text, AttributionSegmentType.Dialog, speaker, voice);

        private static AttributionSegment Narration(string text) =>
            new(text, AttributionSegmentType.Narration, "narrator", "");

        private static IReadOnlyList<AttributionSegment> Align(
            string original, params AttributionSegment[] segments)
        {
            Assert.True(SegmentAligner.TryAlign(original, segments, out var aligned));
            Assert.Equal(original, string.Concat(aligned.Select(s => s.Text)));
            Assert.Equal(segments.Length, aligned.Count);
            return aligned;
        }

        [Fact]
        public void ExactMatch_SlicesOriginal_InterSegmentWhitespaceToPreceding()
        {
            var original = "“No, no!” said the Queen. “Sentence first—verdict afterwards.”";
            var aligned = Align(original,
                Dialog("“No, no!”", "Queen"),
                Narration("said the Queen."),
                Dialog("“Sentence first—verdict afterwards.”", "Queen"));

            Assert.Equal("“No, no!” ", aligned[0].Text);
            Assert.Equal("said the Queen. ", aligned[1].Text);
            Assert.Equal("“Sentence first—verdict afterwards.”", aligned[2].Text);
        }

        [Fact]
        public void MetadataPreserved()
        {
            var aligned = Align("“Hi.” she said.",
                Dialog("“Hi.”", "Alice", "warm"),
                Narration("she said."));

            Assert.Equal(AttributionSegmentType.Dialog, aligned[0].Type);
            Assert.Equal("Alice", aligned[0].Speaker);
            Assert.Equal("warm", aligned[0].VoiceInstructions);
            Assert.Equal(AttributionSegmentType.Narration, aligned[1].Type);
            Assert.Equal("narrator", aligned[1].Speaker);
        }

        [Fact]
        public void StraightQuotesAndHyphens_MatchCurlyOriginal_SlicesKeepOriginalChars()
        {
            var original = "“Sentence first—verdict afterwards.” said the Queen.";
            var aligned = Align(original,
                Dialog("\"Sentence first--verdict afterwards.\"", "Queen"),
                Narration("said the Queen."));

            Assert.Equal("“Sentence first—verdict afterwards.” ", aligned[0].Text);
        }

        [Fact]
        public void WhitespaceRunDrift_Tolerated()
        {
            var original = "He said  nothing.\n“Go.”";
            Align(original, Narration("He said nothing."), Dialog("“Go.”"));
        }

        [Fact]
        public void SingleSegment_WholeParagraph()
        {
            var original = "  It was all narration, nothing more.";
            var aligned = Align(original, Narration("It was all narration, nothing more."));
            Assert.Equal(original, aligned[0].Text);
        }

        [Fact]
        public void ExtraTrailingComma_OnSegmentText_Consumed()
        {
            // gemma lobster: extra comma after the dialog segment's closing text.
            var original = "“You may not have lived much under the sea—” said the Turtle.";
            Align(original,
                Dialog("“You may not have lived much under the sea—”,", "Mock Turtle"),
                Narration("said the Turtle."));
        }

        [Fact]
        public void ExtraTrailingQuoteComma_OnNarrationSegment_Consumed()
        {
            // gemma chimney: stray `", ` appended to the narration segment.
            var original = "down the chimney (a loud crash)—“Now, who did that?”";
            Align(original,
                Narration("down the chimney (a loud crash)—\", "),
                Dialog("“Now, who did that?”", "unknown"));
        }

        [Fact]
        public void DroppedDashAtJoin_UnclaimedCharGoesToPrecedingSlice()
        {
            // ornith chimney/lobster: the — between narration and quote missing from both segments.
            var original = "down the chimney (a loud crash)—“Now, who did that?”";
            var aligned = Align(original,
                Narration("down the chimney (a loud crash)"),
                Dialog("“Now, who did that?”", "unknown"));

            Assert.Equal("down the chimney (a loud crash)—", aligned[0].Text);
            Assert.Equal("“Now, who did that?”", aligned[1].Text);
        }

        [Fact]
        public void DuplicatedLeadingPunctuation_OnSegmentText_Consumed()
        {
            // ornith mouse: duplicated comma at a join.
            var original = "“—I proceed,” said the Mouse.";
            Align(original,
                Dialog("“—I proceed,”", "Mouse"),
                Narration(",said the Mouse."));
        }

        [Fact]
        public void TrailingUnclaimedPunctuation_GoesToLastSlice()
        {
            var original = "“Off with her head!”—";
            var aligned = Align(original, Dialog("“Off with her head!”", "Queen"));
            Assert.Equal(original, aligned[0].Text);
        }

        [Fact]
        public void InSegmentOmission_Fails()
        {
            // Real content dropped inside a segment must not silently apply.
            var original = "Two began in a low voice, “Why the fact is, you see, Miss—”";
            Assert.False(SegmentAligner.TryAlign(original, new[]
            {
                Narration("Two began, "), // dropped "in a low voice"
                Dialog("“Why the fact is, you see, Miss—”", "Two"),
            }, out _));
        }

        [Fact]
        public void DuplicatedContentInsideSegment_Fails()
        {
            // gemma garden: quote text duplicated inside the narration segment.
            var original = "“The Queen!” The gardeners threw themselves flat.";
            Assert.False(SegmentAligner.TryAlign(original, new[]
            {
                Dialog("“The Queen!”", "unknown"),
                Narration("“The Queen!” The gardeners threw themselves flat."),
            }, out _));
        }

        [Fact]
        public void LeftoverWordsAfterLastSegment_Fails()
        {
            var original = "“Go.” said Alice quietly.";
            Assert.False(SegmentAligner.TryAlign(original, new[]
            {
                Dialog("“Go.”"),
                Narration("said Alice"), // "quietly." unclaimed
            }, out _));
        }

        [Fact]
        public void SegmentsOutOfOrder_Fail()
        {
            var original = "“Go.” said Alice.";
            Assert.False(SegmentAligner.TryAlign(original, new[]
            {
                Narration("said Alice."),
                Dialog("“Go.”"),
            }, out _));
        }

        [Fact]
        public void EmptyInputs_Fail()
        {
            Assert.False(SegmentAligner.TryAlign("text", Array.Empty<AttributionSegment>(), out _));
            Assert.False(SegmentAligner.TryAlign("", new[] { Narration("text") }, out _));
        }

        [Fact]
        public void BoundaryInsidePunctuationCluster_Aligns()
        {
            // The LLM may split mid-cluster: `,”` opens segment 2 rather than closing segment 1.
            var original = "“Wait,” she said.";
            var aligned = Align(original, Dialog("“Wait", "Alice"), Narration(",” she said."));
            Assert.Equal("“Wait", aligned[0].Text);
            Assert.Equal(",” she said.", aligned[1].Text);
        }
    }
}
