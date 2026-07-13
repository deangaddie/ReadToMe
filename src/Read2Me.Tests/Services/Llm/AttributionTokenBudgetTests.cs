using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class AttributionTokenBudgetTests
    {
        [Fact]
        public void NoConfiguredLimit_StaysUnlimited()
        {
            Assert.Null(AttributionTokenBudget.ForPassage(null, [new string('x', 5000)]));
        }

        [Fact]
        public void GenerousConfig_IsKept()
        {
            var budget = AttributionTokenBudget.ForPassage(100_000, ["A short line."]);
            Assert.Equal(100_000, budget);
        }

        [Fact]
        public void LongPassage_RaisesBudgetAboveConfig()
        {
            var paragraph = new string('x', 4000);
            var budget = AttributionTokenBudget.ForPassage(512, [paragraph]);

            Assert.NotNull(budget);
            Assert.True(budget > 2000, $"4000 chars must not be answered under 512 tokens (got {budget})");
        }

        [Fact]
        public void BudgetGrowsWithParagraphCount()
        {
            var one = AttributionTokenBudget.ForPassage(256, [new string('x', 800)]);
            var three = AttributionTokenBudget.ForPassage(256, [
                new string('x', 800), new string('x', 800), new string('x', 800)]);

            Assert.True(three > one);
        }

        [Fact]
        public void EmptyPassage_KeepsConfiguredLimit()
        {
            Assert.Equal(4096, AttributionTokenBudget.ForPassage(4096, []));
        }
    }
}
