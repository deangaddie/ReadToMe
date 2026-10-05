using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class EditProgramDescriberTests
    {
        private static EditProgram Program(EditTargetSelector target, EditTransform transform) =>
            new(true, null, target, NodeFilter.All, ParagraphFilter.All, transform);

        [Theory]
        [InlineData(null, CaseMode.Upper, "Edit part titles (whole book) — change to upper case")]
        [InlineData(null, CaseMode.Lower, "Edit part titles (whole book) — change to lower case")]
        [InlineData("PART", CaseMode.Upper, "Edit part titles (whole book) — change text matching \"PART\" to upper case")]
        [InlineData(null, CaseMode.Sentence,
            "Edit part titles (whole book) — change to sentence case — names, numerals and acronyms are kept")]
        [InlineData("PART", CaseMode.Title,
            "Edit part titles (whole book) — change text matching \"PART\" to title case — names, numerals and acronyms are kept")]
        public void Describe_ChangeCase_NamesTheMode(string? pattern, CaseMode mode, string expected)
        {
            var program = Program(EditTargetSelector.PartTitle,
                new EditTransform(TransformKind.ChangeCase, Pattern: pattern, CaseMode: mode));

            Assert.Equal(expected, EditProgramDescriber.Describe(program));
        }

        [Theory]
        [InlineData(0, 5, 0, "5 titles also match — run again targeting part titles")]
        [InlineData(0, 1, 0, "1 title also matches — run again targeting part titles")]
        [InlineData(0, 2, 3, "5 titles also match — run again targeting part and chapter titles")]
        [InlineData(1, 1, 1, "3 titles also match — run again targeting volume, part and chapter titles")]
        public void TitlesAlsoMatch_CountsAndNamesTheMatchingLevels(int volume, int part, int chapter, string expected)
        {
            Assert.Equal(expected, EditProgramDescriber.TitlesAlsoMatch(new TitleMatchCounts(volume, part, chapter)));
        }

        [Fact]
        public void TitlesAlsoMatch_NoMatchingTitles_IsNoWarning()
        {
            Assert.Null(EditProgramDescriber.TitlesAlsoMatch(new TitleMatchCounts(0, 0, 0)));
        }
    }
}
