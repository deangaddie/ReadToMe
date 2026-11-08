using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class EditSourceCatalogTests
    {
        private static readonly EditTargetSelector[] Targets = Enum.GetValues<EditTargetSelector>();

        [Fact]
        public void Names_ListEveryNameFromTheSpec()
        {
            string[] expected =
            [
                "p1", "p2", "p3", "p4", "p5", "p_end1", "p_end2", "p_end3", "p_end4", "p_end5",
                "prev", "next", "chapter", "part", "volume",
                "ch1", "ch2", "ch3", "ch4", "ch5", "ch_end1", "ch_end2", "ch_end3", "ch_end4", "ch_end5",
                "pt1", "pt2", "pt3", "pt4", "pt5", "pt_end1", "pt_end2", "pt_end3", "pt_end4", "pt_end5",
            ];
            Assert.Equal(expected, EditSourceCatalog.Names);
        }

        public static TheoryData<string> AllNames() => new(EditSourceCatalog.Names);

        [Theory]
        [MemberData(nameof(AllNames))]
        public void EveryName_IsReadableOrRejectedWithAReason_ForEveryTarget(string name)
        {
            foreach (var target in Targets)
            {
                var reason = EditSourceCatalog.Reject(name, target, "unknown", out var source);
                Assert.Equal(name, source.Name);
                if (reason != null)
                    Assert.NotEqual("unknown", reason);
            }
        }

        [Theory]
        [MemberData(nameof(AllNames))]
        public void EveryName_HasALabel(string name)
        {
            Assert.True(EditSourceCatalog.TryParse(name, out var source));
            Assert.False(string.IsNullOrWhiteSpace(EditSourceCatalog.Label(source)));
        }

        [Theory]
        [InlineData("p1", "Paragraph 1")]
        [InlineData("p_end1", "Last paragraph")]
        [InlineData("p_end2", "Second-last paragraph")]
        [InlineData("prev", "Previous paragraph")]
        [InlineData("next", "Next paragraph")]
        [InlineData("chapter", "Chapter title")]
        [InlineData("part", "Part title")]
        [InlineData("volume", "Volume title")]
        [InlineData("ch1", "Chapter 1 title")]
        [InlineData("ch_end1", "Last chapter title")]
        [InlineData("pt1", "Part 1 title")]
        [InlineData("pt_end3", "Third-last part title")]
        public void Label_IsThePlainGloss(string name, string label)
        {
            Assert.True(EditSourceCatalog.TryParse(name, out var source));
            Assert.Equal(label, EditSourceCatalog.Label(source));
        }

        [Theory]
        // chapter title: own paragraphs, enclosing part and volume
        [InlineData(EditTargetSelector.ChapterTitle, "p1 p_end5 part volume", "prev next chapter ch1 ch_end1 pt1 pt_end1")]
        // part title: its chapters' titles, enclosing volume
        [InlineData(EditTargetSelector.PartTitle, "ch1 ch_end5 volume", "p1 p_end1 prev next chapter part pt1 pt_end1")]
        // volume title: chapters across the volume, its parts' titles
        [InlineData(EditTargetSelector.VolumeTitle, "ch1 ch_end2 pt1 pt_end5", "p1 p_end1 prev next chapter part volume")]
        // paragraph text: enclosing titles, chapter paragraphs, neighbours
        [InlineData(EditTargetSelector.ParagraphText, "p1 p_end1 prev next chapter part volume", "ch1 ch_end1 pt1 pt_end1")]
        public void Reject_FollowsThePerTargetCatalogue(EditTargetSelector target, string readable, string unreadable)
        {
            foreach (var name in readable.Split(' '))
                Assert.Null(EditSourceCatalog.Reject(name, target, "unknown", out _));
            foreach (var name in unreadable.Split(' '))
                Assert.NotNull(EditSourceCatalog.Reject(name, target, "unknown", out _));
        }

        [Fact]
        public void Reject_VolumeReadingAParagraph_SaysToRenameTheChapterFirst()
        {
            Assert.Equal("Volume titles can't read paragraphs — rename the chapter first",
                EditSourceCatalog.Reject("p1", EditTargetSelector.VolumeTitle, "unknown", out _));
        }

        [Theory]
        [InlineData("p6")]
        [InlineData("p_end6")]
        [InlineData("ch9")]
        [InlineData("pt_end12")]
        [InlineData("p0")]
        public void Reject_PositionPastFive_IsOutOfReach(string name)
        {
            Assert.False(EditSourceCatalog.TryParse(name, out _));
            foreach (var target in Targets)
                Assert.Equal($"{name} is out of reach: sources count only 1 to 5 from the start or end",
                    EditSourceCatalog.Reject(name, target, "unknown", out _));
        }

        [Theory]
        [InlineData("title")]
        [InlineData("paragraph1")]
        [InlineData("P1")]
        public void Reject_UnknownName_UsesTheCallersReason(string name)
        {
            Assert.Equal("unknown", EditSourceCatalog.Reject(name, EditTargetSelector.ChapterTitle, "unknown", out _));
        }

        [Fact]
        public void TemplateTokens_AreBareIdentifiersInOrder_Distinct()
        {
            Assert.Equal(["p1", "n", "p2", "old"],
                EditSourceCatalog.TemplateTokens("Chapter {p1}{n}, {p2} {p1} {old} {1} { p3 } {a-b} {}"));
        }

        [Fact]
        public void JsonSchema_SourcesEnum_IsEveryName()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(EditProgramSchema.JsonSchema);
            var transform = doc.RootElement.GetProperty("properties").GetProperty("transform");
            var names = transform.GetProperty("properties").GetProperty("sources")
                .GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
            Assert.Equal(EditSourceCatalog.Names, names);
            Assert.Contains("sources", transform.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        }
    }
}
