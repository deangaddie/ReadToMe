using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.Transcription;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class CarrierAlignerTests
    {
        private static TranscribedWord W(string word, double start, double end) => new(word, start, end);

        [Fact]
        public void FindBoundary_ExactMatch_ReturnsGapAroundBoundary()
        {
            var words = new[]
            {
                W("The", 0.0, 0.3),
                W("quick", 0.35, 0.7),
                W("brown", 0.75, 1.1),
                W("fox.", 1.15, 1.5),
                W("Come.", 1.9, 2.3),
            };

            var boundary = CarrierAligner.FindBoundary("The quick brown fox.", words);

            Assert.NotNull(boundary);
            Assert.Equal(1.5, boundary.Value.CarrierEnd);
            Assert.Equal(1.9, boundary.Value.TargetStart);
        }

        [Fact]
        public void FindBoundary_NumberCanonicalization_MatchesDigitAgainstWord()
        {
            // Carrier says "one", whisper transcribes the digit — both normalize to "1".
            var words = new[]
            {
                W("Chapter", 0.0, 0.5),
                W("1", 0.55, 0.8),
                W("begins", 0.85, 1.3),
                W("now.", 1.35, 1.7),
                W("1.", 2.1, 2.4),
            };

            var boundary = CarrierAligner.FindBoundary("Chapter one begins now.", words);

            Assert.NotNull(boundary);
            Assert.Equal(1.7, boundary.Value.CarrierEnd);
            Assert.Equal(2.1, boundary.Value.TargetStart);
        }

        [Fact]
        public void FindBoundary_ApostropheWord_YieldsMultipleTokensFromOneWord()
        {
            // "don't" normalizes to two tokens (don, t) mapped to the same whisper word.
            var words = new[]
            {
                W("I", 0.0, 0.2),
                W("don't", 0.25, 0.6),
                W("know", 0.65, 1.0),
                W("yet.", 1.05, 1.4),
                W("Come.", 1.8, 2.2),
            };

            var boundary = CarrierAligner.FindBoundary("I don't know yet.", words);

            Assert.NotNull(boundary);
            Assert.Equal(1.4, boundary.Value.CarrierEnd);
            Assert.Equal(1.8, boundary.Value.TargetStart);
        }

        [Fact]
        public void FindBoundary_MinorTranscriptionError_StillAccepted()
        {
            // One carrier word misheard out of eight — distance 1/8 is under the 0.4 gate.
            var words = new[]
            {
                W("She", 0.0, 0.2),
                W("walked", 0.25, 0.6),
                W("slowly", 0.65, 1.0),
                W("threw", 1.05, 1.3),   // misheard "through"
                W("the", 1.35, 1.5),
                W("quiet", 1.55, 1.9),
                W("winter", 1.95, 2.3),
                W("garden.", 2.35, 2.8),
                W("One.", 3.2, 3.5),
            };

            var boundary = CarrierAligner.FindBoundary(
                "She walked slowly through the quiet winter garden.", words);

            Assert.NotNull(boundary);
            Assert.Equal(2.8, boundary.Value.CarrierEnd);
            Assert.Equal(3.2, boundary.Value.TargetStart);
        }

        [Fact]
        public void FindBoundary_ExtraTranscribedWordInCarrier_SplitSearchAbsorbsIt()
        {
            // Whisper hallucinated an extra filler word inside the carrier; the split index
            // drifts to N+1 and still matches.
            var words = new[]
            {
                W("She", 0.0, 0.2),
                W("walked", 0.25, 0.6),
                W("slowly", 0.65, 1.0),
                W("through", 1.05, 1.3),
                W("the", 1.35, 1.5),
                W("uh", 1.5, 1.55),      // hallucinated
                W("quiet", 1.6, 1.9),
                W("winter", 1.95, 2.3),
                W("garden.", 2.35, 2.8),
                W("One.", 3.2, 3.5),
            };

            var boundary = CarrierAligner.FindBoundary(
                "She walked slowly through the quiet winter garden.", words);

            Assert.NotNull(boundary);
            Assert.Equal(2.8, boundary.Value.CarrierEnd);
            Assert.Equal(3.2, boundary.Value.TargetStart);
        }

        [Fact]
        public void FindBoundary_CompletelyDifferentTranscription_ReturnsNull()
        {
            var words = new[]
            {
                W("something", 0.0, 0.5),
                W("else", 0.55, 0.9),
                W("entirely", 0.95, 1.5),
                W("here", 1.55, 1.9),
                W("now", 1.95, 2.3),
            };

            var boundary = CarrierAligner.FindBoundary("The quick brown fox jumps.", words);

            Assert.Null(boundary);
        }

        [Fact]
        public void FindBoundary_BoundaryInsideSingleTranscribedWord_ReturnsNull()
        {
            // Best split lands between the "don" and "t" tokens of one whisper word —
            // there is no inter-word gap to cut in.
            var words = new[]
            {
                W("well", 0.0, 0.3),
                W("now", 0.35, 0.6),
                W("don't", 0.65, 1.0),
            };

            var boundary = CarrierAligner.FindBoundary("well now don", words);

            Assert.Null(boundary);
        }

        [Fact]
        public void FindBoundary_EmptyCarrier_ReturnsNull()
        {
            var words = new[] { W("hello", 0.0, 0.5), W("world", 0.6, 1.0) };

            Assert.Null(CarrierAligner.FindBoundary("", words));
            Assert.Null(CarrierAligner.FindBoundary("   ", words));
        }

        [Fact]
        public void FindBoundary_NoTranscribedWords_ReturnsNull()
        {
            Assert.Null(CarrierAligner.FindBoundary("The quick brown fox.", []));
        }

        [Fact]
        public void FindBoundary_SingleTranscribedToken_ReturnsNull()
        {
            // One token can't be split into carrier + target.
            var words = new[] { W("hello", 0.0, 0.5) };

            Assert.Null(CarrierAligner.FindBoundary("hello", words));
        }

        [Fact]
        public void FindBoundary_PunctuationOnlyTranscribedWord_ContributesNoTokens()
        {
            var words = new[]
            {
                W("The", 0.0, 0.3),
                W("quick", 0.35, 0.7),
                W("...", 0.7, 0.75),     // normalizes to nothing
                W("brown", 0.8, 1.1),
                W("fox.", 1.15, 1.5),
                W("Come.", 1.9, 2.3),
            };

            var boundary = CarrierAligner.FindBoundary("The quick brown fox.", words);

            Assert.NotNull(boundary);
            Assert.Equal(1.5, boundary.Value.CarrierEnd);
            Assert.Equal(1.9, boundary.Value.TargetStart);
        }
    }
}
