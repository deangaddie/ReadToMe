using Read2Me.Services.Characters.ChapterPass;
using Xunit;

namespace Read2Me.Tests.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass's context budget (spec §4.5): a character ceiling on the whole prompt, and a
    /// front-trim that drops the oldest half of the lines before the current paragraph.
    /// </summary>
    public class ChapterPassBudgetTests
    {
        [Fact]
        public void Trims_only_when_the_prompt_exceeds_the_ceiling()
        {
            Assert.Equal(40_000, ChapterPassBudget.MaxPromptChars);
            Assert.False(ChapterPassBudget.NeedsTrim(10_000, 29_000, 1_000));
            Assert.True(ChapterPassBudget.NeedsTrim(10_000, 29_000, 1_001));
        }

        [Fact]
        public void Each_trim_drops_the_oldest_half_of_the_lines_before_the_current_paragraph()
        {
            var starts = new List<int>();
            var trimStart = 0;
            for (var n = 0; n < 8; n++)
                starts.Add(trimStart = ChapterPassBudget.NextTrimStart(trimStart, currentK: 100));

            Assert.Equal([50, 75, 88, 94, 97, 99, 100, 100], starts);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0, 1)]
        [InlineData(7, 8)]
        [InlineData(7, 7)]
        [InlineData(3, 1000)]
        public void A_trim_never_passes_the_current_paragraph(int trimStart, int currentK)
        {
            var next = ChapterPassBudget.NextTrimStart(trimStart, currentK);

            Assert.InRange(next, trimStart, currentK);
            if (currentK > trimStart)
                Assert.True(next > trimStart, "a trim with lines to drop must drop at least one");
        }
    }
}
