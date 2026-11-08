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
        [InlineData(CaseMode.Sentence,
            "Edit paragraph text (all paragraphs) — change all-caps words to sentence case — names, numerals and acronyms are kept")]
        [InlineData(CaseMode.Lower, "Edit paragraph text (all paragraphs) — change all-caps words to lower case")]
        public void Describe_ChangeCase_ReadyMadeAllCapsPattern_IsNamedInPlainWords(CaseMode mode, string expected)
        {
            var program = Program(EditTargetSelector.ParagraphText,
                new EditTransform(TransformKind.ChangeCase, Pattern: EditProgramSchema.AllCapsPattern, CaseMode: mode));

            Assert.Equal(expected, EditProgramDescriber.Describe(program));
        }

        private static EditSource Src(string name) => EditSourceCatalog.TryParse(name, out var s) ? s : throw new ArgumentException(name);

        [Fact]
        public void Describe_SetTemplate_ReadingSources_EndsWithTheLabelsInPlanOrder()
        {
            var program = Program(EditTargetSelector.ChapterTitle, new EditTransform(
                TransformKind.SetTemplate, Template: "Chapter {p1}, {p2}", Sources: [Src("p1"), Src("p2")]));

            Assert.Equal(
                "Edit chapter titles (whole book) — set to template \"Chapter {p1}, {p2}\" — reads p1 (paragraph 1), p2 (paragraph 2)",
                EditProgramDescriber.Describe(program));
        }

        [Fact]
        public void Describe_Llm_ReadingSources_EndsWithTheLabelsInPlanOrder()
        {
            var program = Program(EditTargetSelector.ChapterTitle, new EditTransform(
                TransformKind.Llm, Instruction: "title it", Sources: [Src("p2"), Src("p_end1"), Src("chapter")]));

            Assert.Equal(
                "Edit chapter titles (whole book) — AI edit: \"title it\" — reads p2 (paragraph 2), p_end1 (last paragraph), chapter (chapter title)",
                EditProgramDescriber.Describe(program));
        }

        [Fact]
        public void Describe_NoSources_HasNoReadsSuffix()
        {
            var program = Program(EditTargetSelector.ChapterTitle, new EditTransform(
                TransformKind.SetTemplate, Template: "Chapter {n}"));

            Assert.Equal("Edit chapter titles (whole book) — set to template \"Chapter {n}\"",
                EditProgramDescriber.Describe(program));
        }

        [Fact]
        public void Describe_ChangeCase_ReadyMadePatternWithStraightApostrophes_IsStillNamedInPlainWords()
        {
            // With thinking on, qwen-28b may copy the escaped ’ as '.
            var copied = EditProgramSchema.AllCapsPattern.Replace(EditProgramSchema.EscapedTypographicApostrophe, "'");
            var program = Program(EditTargetSelector.ParagraphText,
                new EditTransform(TransformKind.ChangeCase, Pattern: copied, CaseMode: CaseMode.Upper));

            Assert.EndsWith("— change all-caps words to upper case", EditProgramDescriber.Describe(program));
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
