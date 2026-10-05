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
    }
}
