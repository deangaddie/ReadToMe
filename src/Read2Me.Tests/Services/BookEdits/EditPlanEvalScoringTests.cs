using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class EditPlanEvalScoringTests
    {
        private static readonly IReadOnlyDictionary<string, string> Roman = new Dictionary<string, string>
        {
            ["p1"] = "CHAPTER I.",
            ["p2"] = "The Period.",
            ["chapter"] = "Chapter 1",
        };

        [Fact]
        public void Render_FillsSourceTokensFromTheFixture()
        {
            Assert.Equal("CHAPTER I.: The Period.", EditPlanEvalScoring.Render("{p1}: {p2}", Roman));
        }

        [Fact]
        public void Render_KeepsNAndOldWorkingBesideSources()
        {
            Assert.Equal("7. It was the best of times | Chapter 1",
                EditPlanEvalScoring.Render("{n}. {old} | {chapter}", Roman, n: 7, old: "It was the best of times"));
        }

        [Fact]
        public void Render_LeavesATokenTheFixtureLacksLiteral()
        {
            Assert.Equal("{p9}", EditPlanEvalScoring.Render("{p9}", Roman));
        }

        [Fact]
        public void SameSources_ComparesAsASet()
        {
            Assert.True(EditPlanEvalScoring.SameSources(["p1", "p2"], [Source("p2"), Source("p1")]));
            Assert.True(EditPlanEvalScoring.SameSources(["p1"], [Source("p1"), Source("p1")]));
        }

        [Fact]
        public void SameSources_RejectsAMissingOrExtraSource()
        {
            Assert.False(EditPlanEvalScoring.SameSources(["p1", "p2"], [Source("p1")]));
            Assert.False(EditPlanEvalScoring.SameSources(["p1"], [Source("p1"), Source("p2")]));
        }

        [Fact]
        public void MentionsEach_NeedsEveryNameAsAWholeWord()
        {
            Assert.True(EditPlanEvalScoring.MentionsEach("Use the number in p1 and the title in p2.", ["p1", "p2"]));
            Assert.False(EditPlanEvalScoring.MentionsEach("Use the number in p1.", ["p1", "p2"]));
            Assert.False(EditPlanEvalScoring.MentionsEach("Use p10 and p2.", ["p1", "p2"]));
            Assert.True(EditPlanEvalScoring.MentionsEach("the number written in (p_end1), as a digit", ["p_end1"]));
        }

        private static EditSource Source(string name)
        {
            Assert.True(EditSourceCatalog.TryParse(name, out var source));
            return source;
        }
    }
}
