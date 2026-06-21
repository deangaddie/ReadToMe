using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class WerComparerTests
    {
        private readonly WerComparer _comparer = new();

        [Fact]
        public void Identical_ReturnsZero()
        {
            Assert.Equal(0.0, _comparer.Compute("the quick brown fox", "the quick brown fox"));
        }

        [Fact]
        public void CaseDifferenceOnly_ReturnsZero()
        {
            Assert.Equal(0.0, _comparer.Compute("The Quick Brown Fox", "the quick brown fox"));
        }

        [Fact]
        public void PunctuationDifferenceOnly_ReturnsZero()
        {
            Assert.Equal(0.0, _comparer.Compute("Hello, world!", "hello world"));
        }

        [Theory]
        [InlineData("21", "twenty one")]
        [InlineData("3 cats", "three cats")]
        [InlineData("105", "one hundred and five")]
        public void NumeralEquivalence_ReturnsZero(string reference, string hypothesis)
        {
            Assert.Equal(0.0, _comparer.Compute(reference, hypothesis));
        }

        [Fact]
        public void OneSubstitutionInFiveTokens_ReturnsPointTwo()
        {
            // reference: 5 tokens, one replaced in the hypothesis
            Assert.Equal(0.2, _comparer.Compute("alpha beta gamma delta epsilon", "alpha beta XXXX delta epsilon"));
        }

        [Fact]
        public void EmptyReferenceNonEmptyHypothesis_ReturnsOne()
        {
            Assert.Equal(1.0, _comparer.Compute("", "something here"));
        }

        [Fact]
        public void BothEmpty_ReturnsZero()
        {
            Assert.Equal(0.0, _comparer.Compute("", ""));
        }
    }
}
