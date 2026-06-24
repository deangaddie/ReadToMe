using Read2Me.Services.Text;
using Xunit;

namespace Read2Me.Tests.Services.Text
{
    public class TextSubstitutionStepImplTests
    {
        [Fact]
        public void Replaces_MatchingSubstring()
        {
            var step = new TextSubstitutionStepImpl("(", ",");
            Assert.Equal("hello, world)", step.Process("hello( world)"));
        }

        [Fact]
        public void Leaves_NonMatchingText_Unchanged()
        {
            var step = new TextSubstitutionStepImpl("xyz", "abc");
            Assert.Equal("hello world", step.Process("hello world"));
        }

        [Fact]
        public void EmptyFrom_ThrowsArgumentException()
        {
            // string.Replace with empty oldValue throws ArgumentException
            var step = new TextSubstitutionStepImpl("", "X");
            Assert.Throws<ArgumentException>(() => step.Process("abc"));
        }

        [Fact]
        public void EmptyTo_DeletesMatchingSubstring()
        {
            var step = new TextSubstitutionStepImpl("unwanted", "");
            Assert.Equal("hello ", step.Process("hello unwanted"));
        }

        [Fact]
        public void EmptyInput_ReturnsEmpty()
        {
            var step = new TextSubstitutionStepImpl("abc", "xyz");
            Assert.Equal("", step.Process(""));
        }

        [Fact]
        public void Replacement_IsCaseSensitive_Ordinal()
        {
            var step = new TextSubstitutionStepImpl("Dr.", "Doctor");
            Assert.Equal("Doctor Smith", step.Process("Dr. Smith"));
            Assert.Equal("dr. Smith", step.Process("dr. Smith")); // no match — ordinal
        }
    }
}
