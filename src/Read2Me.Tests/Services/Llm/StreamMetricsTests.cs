using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class StreamMetricsTests
    {
        [Theory]
        [InlineData("", 0)]
        [InlineData("abcd", 1)]
        [InlineData("abcde", 2)]
        public void EstimateTokens_RoundsUp(string text, int expected) =>
            Assert.Equal(expected, StreamMetrics.EstimateTokens(text));

        [Fact]
        public void AddOutput_Accumulates()
        {
            var m = new StreamMetrics("");
            m.AddOutput("abcd");
            m.AddOutput("abcd");
            Assert.Equal(2, m.TokensOut);
        }

        [Fact]
        public void TokensPerSecond_ZeroElapsed_ReturnsZero()
        {
            var m = new StreamMetrics("");
            m.AddOutput("abcd");
            Assert.Equal(0, m.TokensPerSecond(0));
        }

        [Fact]
        public void TokensPerSecond_DividesOutputByElapsed()
        {
            var m = new StreamMetrics("");
            m.AddOutput("abcdabcdabcdabcd"); // 16 chars -> 4 tokens
            Assert.Equal(2.0, m.TokensPerSecond(2.0));
        }

        [Fact]
        public void TokensIn_SetFromPromptOnConstruction()
        {
            var m = new StreamMetrics("abcdabcd"); // 8 chars -> 2 tokens
            Assert.Equal(2, m.TokensIn);
        }
    }
}
