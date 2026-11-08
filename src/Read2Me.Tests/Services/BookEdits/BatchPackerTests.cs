using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class BatchPackerTests
    {
        private static EditTarget Target(string value, params string[] sources) =>
            new(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), value, "Chapter", 1, null, null)
            {
                Sources = sources.Select((s, i) => new EditSourceValue($"p{i + 1}", s, null)).ToList(),
            };

        private static int[][] Pack(params int[] sizes) =>
            BatchPacker.Pack(sizes).Batches.Select(b => b.ToArray()).ToArray();

        [Fact]
        public void Pack_EmptyInput_NoBatches()
        {
            var plan = BatchPacker.Pack([]);
            Assert.Empty(plan.Batches);
            Assert.Empty(plan.TooLong);
        }

        [Fact]
        public void Pack_CapsABatchAtEightItems()
        {
            Assert.Equal([[0, 1, 2, 3, 4, 5, 6, 7], [8, 9]], Pack(Enumerable.Repeat(10, 10).ToArray()));
        }

        [Fact]
        public void Pack_ClosesABatchAtTheCharacterBudget()
        {
            // 5000 + 5000 = 10000 fits; a third 5000 would pass 12000.
            Assert.Equal([[0, 1], [2, 3]], Pack(5000, 5000, 5000, 5000));
            Assert.Equal([[0, 1]], Pack(6000, 6000)); // exactly the budget fits
        }

        [Fact]
        public void Pack_AnItemOverBudgetGoesAlone_AndKeepsOrder()
        {
            Assert.Equal([[0], [1], [2, 3]], Pack(100, 20000, 100, 100));
        }

        [Fact]
        public void Pack_AnItemOverTheCeilingIsTooLong_AndLeavesTheBatchesAlone()
        {
            var plan = BatchPacker.Pack([100, 60001, 100]);
            Assert.Equal([1], plan.TooLong);
            Assert.Equal([[0, 2]], plan.Batches.Select(b => b.ToArray()));
            Assert.Empty(BatchPacker.Pack([60000]).TooLong);
        }

        [Fact]
        public void Size_CountsTextAndSourceValues()
        {
            Assert.Equal(2 + 3 + 4, BatchPacker.Size(Target("ab", "abc", "abcd")));
        }

        [Fact]
        public void PlanFor_SkipsRowsWithAMissingSource_AndIndexesIntoTheTargets()
        {
            var failed = Target("x") with { Sources = [new("p1", null, "p1 is missing: the chapter has no paragraphs")] };
            var plan = BatchPacker.PlanFor([Target("a"), failed, Target("b")]);
            Assert.Equal([[0, 2]], plan.Batches.Select(b => b.ToArray()));
            Assert.Empty(plan.TooLong);
        }

        [Fact]
        public void RequestCount_MatchesThePackedBatches()
        {
            var targets = Enumerable.Range(0, 10).Select(_ => Target("a")).ToList();
            Assert.Equal(2, BatchPacker.RequestCount(targets));
            Assert.Equal(0, BatchPacker.RequestCount([]));
        }
    }
}
