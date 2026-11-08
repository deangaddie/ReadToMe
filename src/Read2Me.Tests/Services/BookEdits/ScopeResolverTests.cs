using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.BookEdits;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class ScopeResolverTests : ProjectDbTestBase
    {
        private readonly ScopeResolver _sut;
        private readonly ProjectFolderId _folder;

        public ScopeResolverTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _sut = new ScopeResolver(new ProjectReader(session, NullLogger<ProjectReader>.Instance));
            _folder = new ProjectFolderId(FolderName);
        }

        private static EditProgram Program(
            EditTargetSelector target,
            TransformKind kind = TransformKind.Llm,
            NodeFilter? nodeFilter = null,
            ParagraphFilter? paragraphFilter = null) =>
            new(true, null, target,
                nodeFilter ?? NodeFilter.All,
                paragraphFilter ?? ParagraphFilter.All,
                new EditTransform(kind, Instruction: "do it"));

        /// <summary>Seeds: vol > [ch1 (2 paragraphs, first has pause before text), ch2 (1 paragraph)].</summary>
        private async Task<BookHierarchyBuilder> SeedBookAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddChapter("ch1", c => c
                    .AddParagraph("p1", p => p
                        .AddPause("pause1")
                        .AddNarration("i1", "t is a truth universally acknowledged."))
                    .AddParagraph("p2", p => p.AddNarration("i2", "Second paragraph.")))
                .AddChapter("ch2", c => c
                    .AddParagraph("p3", p => p.AddNarration("i3", "Opening of chapter two."))))
                .BuildAsync();
            return b;
        }

        [Fact]
        public async Task Resolve_ChapterTitles_AllChapters()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Program(EditTargetSelector.ChapterTitle));

            Assert.Equal(2, targets.Count);
            Assert.All(targets, t => Assert.Equal(BookEditTargetKind.ChapterTitle, t.Kind));
            Assert.Equal(b.ChapterId("ch1"), targets[0].Id);
            Assert.Equal("ch1", targets[0].CurrentValue);
            Assert.Equal(1, targets[0].OrdinalInScope);
            Assert.Equal(2, targets[1].OrdinalInScope);
        }

        [Fact]
        public async Task Resolve_OrdinalRange_FiltersChapters()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.ChapterTitle, nodeFilter: new NodeFilter(2, 2, null)));

            Assert.Single(targets);
            Assert.Equal(b.ChapterId("ch2"), targets[0].Id);
        }

        [Fact]
        public async Task Resolve_TitleRegex_FiltersChapters()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.ChapterTitle, nodeFilter: new NodeFilter(null, null, "^ch1$")));

            Assert.Single(targets);
            Assert.Equal(b.ChapterId("ch1"), targets[0].Id);
        }

        private static ParagraphFilter Where(params EditPredicate[] predicates) => new(predicates);

        [Fact]
        public async Task Resolve_FirstParagraphOpeningItem_SkipsPauses()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.ParagraphText,
                    paragraphFilter: Where(
                        new EditPredicate(PredicateField.ParagraphOrdinal, PredicateOp.Eq, 1),
                        new EditPredicate(PredicateField.ItemOrdinal, PredicateOp.Eq, 1))));

            Assert.Equal(2, targets.Count); // one per chapter
            Assert.Equal(b.ItemId("i1"), targets[0].Id);
            Assert.Equal("t is a truth universally acknowledged.", targets[0].CurrentValue);
            Assert.Equal(b.ChapterId("ch1"), targets[0].ChapterId);
            Assert.Equal(b.ParagraphId("p1"), targets[0].ParagraphId);
            Assert.Equal(b.ItemId("i3"), targets[1].Id);
        }

        [Fact]
        public async Task Resolve_SecondParagraph_OnlyChaptersThatHaveOne()
        {
            var b = await SeedBookAsync(); // ch1 has 2 paragraphs, ch2 has 1
            var targets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.ParagraphText,
                    paragraphFilter: Where(new EditPredicate(PredicateField.ParagraphOrdinal, PredicateOp.Eq, 2))));

            Assert.Single(targets);
            Assert.Equal(b.ItemId("i2"), targets[0].Id);
        }

        [Fact]
        public async Task Resolve_LastParagraph_ViaOrdinalFromEnd()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.ParagraphText,
                    paragraphFilter: Where(new EditPredicate(PredicateField.ParagraphOrdinalFromEnd, PredicateOp.Eq, 1))));

            Assert.Equal(2, targets.Count);
            Assert.Equal(b.ItemId("i2"), targets[0].Id);
            Assert.Equal(b.ItemId("i3"), targets[1].Id);
        }

        [Fact]
        public async Task Resolve_AllParagraphText_ReturnsEveryContentItem()
        {
            await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Program(EditTargetSelector.ParagraphText));

            Assert.Equal(3, targets.Count);
        }

        [Fact]
        public async Task Resolve_TextRegex_FiltersItems()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.ParagraphText,
                    paragraphFilter: Where(new EditPredicate(PredicateField.Text, PredicateOp.Regex, Regex: "Second"))));

            Assert.Single(targets);
            Assert.Equal(b.ItemId("i2"), targets[0].Id);
        }

        [Fact]
        public async Task Resolve_VolumeTitles_ReturnsVolume()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Program(EditTargetSelector.VolumeTitle));

            Assert.Single(targets);
            Assert.Equal(b.VolumeId("vol"), targets[0].Id);
            Assert.Equal("vol", targets[0].CurrentValue);
        }

        [Fact]
        public async Task Resolve_NullPartTitle_SkippedForRegexReplace_IncludedForTemplate()
        {
            await SeedBookAsync(); // implicit part has null title

            var regexTargets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.PartTitle, TransformKind.RegexReplace));
            Assert.Empty(regexTargets);

            var templateTargets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.PartTitle, TransformKind.SetTemplate));
            Assert.Single(templateTargets);
            Assert.Equal(string.Empty, templateTargets[0].CurrentValue);
        }

        [Fact]
        public async Task Resolve_DisplayPath_IncludesChapterNumberAndParagraph()
        {
            await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Program(EditTargetSelector.ParagraphText,
                    paragraphFilter: Where(new EditPredicate(PredicateField.ParagraphOrdinal, PredicateOp.Eq, 1))));

            Assert.Contains("Chapter 1", targets[0].DisplayPath);
            Assert.Contains("¶1", targets[0].DisplayPath);
        }

        [Fact]
        public async Task Resolve_EmptyBook_ReturnsNoTargets()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

            var targets = await _sut.ResolveAsync(_folder, Program(EditTargetSelector.ChapterTitle));
            Assert.Empty(targets);
        }

        private static EditProgram Pattern(EditTargetSelector target, TransformKind kind, string? pattern) =>
            new(true, null, target, NodeFilter.All, ParagraphFilter.All,
                kind == TransformKind.ChangeCase
                    ? new EditTransform(kind, pattern, CaseMode: CaseMode.Upper)
                    : new EditTransform(kind, pattern, Replacement: "x"));

        [Theory]
        [InlineData(TransformKind.RegexReplace)]
        [InlineData(TransformKind.ChangeCase)]
        public async Task Resolve_PatternTransform_DropsItemsThePatternDoesNotMatch(TransformKind kind)
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Pattern(EditTargetSelector.ParagraphText, kind, "paragraph|chapter"));

            Assert.Equal([b.ItemId("i2"), b.ItemId("i3")], targets.Select(t => t.Id));
            Assert.Equal([1, 2], targets.Select(t => t.OrdinalInScope));
        }

        [Fact]
        public async Task Resolve_PatternTransform_DropsTitlesThePatternDoesNotMatch()
        {
            var b = await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Pattern(EditTargetSelector.ChapterTitle, TransformKind.ChangeCase, "2"));

            Assert.Equal([b.ChapterId("ch2")], targets.Select(t => t.Id));
        }

        [Fact]
        public async Task Resolve_ChangeCaseWithoutPattern_KeepsEveryTarget()
        {
            await SeedBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Pattern(EditTargetSelector.ParagraphText, TransformKind.ChangeCase, null));

            Assert.Equal(3, targets.Count);
        }

        [Fact]
        public async Task Resolve_LlmTransform_IsNotPrunedByItsPattern()
        {
            await SeedBookAsync();
            var program = new EditProgram(true, null, EditTargetSelector.ParagraphText, NodeFilter.All, ParagraphFilter.All,
                new EditTransform(TransformKind.Llm, Pattern: "nothing matches this", Instruction: "do it"));

            Assert.Equal(3, (await _sut.ResolveAsync(_folder, program)).Count);
        }

        [Fact]
        public async Task Resolve_PatternThatTimesOut_KeepsTheTarget()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch1", c => c
                    .AddParagraph("p1", p => p.AddNarration("i1", new string('a', 40) + "!"))))
                .BuildAsync();

            var targets = await _sut.ResolveAsync(_folder,
                Pattern(EditTargetSelector.ParagraphText, TransformKind.RegexReplace, "^(a|aa)+$"));

            Assert.Equal([b.ItemId("i1")], targets.Select(t => t.Id));
        }

        // ── Edit sources ──────────────────────────────────────────────────────

        private static EditProgram Reading(
            EditTargetSelector target, string names, NodeFilter? nodeFilter = null, ParagraphFilter? paragraphFilter = null) =>
            new(true, null, target, nodeFilter ?? NodeFilter.All, paragraphFilter ?? ParagraphFilter.All,
                new EditTransform(TransformKind.SetTemplate, Template: "{old}",
                    Sources: names.Split(' ').Select(n => EditSourceCatalog.TryParse(n, out var s) ? s : throw new ArgumentException(n)).ToList()));

        /// <summary>
        /// Book One › The First Part › [A: pause-only ¶, "  CHAPTER I.  ", "The" + pause + "Period.", "It was the best of times."],
        ///                              [(untitled): "Only one."]
        ///           › The Second Part › [C: no paragraphs]
        /// Book Two › (lone untitled part) › [D: "Alone."]
        /// </summary>
        private async Task SeedSourceBookAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b
                .AddVolume("Book One", v => v
                    .AddPart("The First Part", p => p
                        .AddChapter("A", c => c
                            .AddParagraph("a0", pp => pp.AddPause("a0-pause"))
                            .AddParagraph("a1", pp => pp.AddNarration("a1-i", "  CHAPTER I.  "))
                            .AddParagraph("a2", pp => pp
                                .AddNarration("a2-i1", "The")
                                .AddPause("a2-pause")
                                .AddNarration("a2-i2", "Period. "))
                            .AddParagraph("a3", pp => pp.AddNarration("a3-i", "It was the best of times.")))
                        .AddChapter(null, c => c
                            .AddParagraph("b1", pp => pp.AddNarration("b1-i", "Only one."))))
                    .AddPart("The Second Part", p => p.AddChapter("C")))
                .AddVolume("Book Two", v => v
                    .AddChapter("D", c => c.AddParagraph("d1", pp => pp.AddNarration("d1-i", "Alone."))))
                .BuildAsync();
        }

        private static (string Name, string? Value, string? Missing)[] Read(EditTarget target) =>
            target.Sources.Select(s => (s.Name, s.Value, s.MissingReason)).ToArray();

        [Fact]
        public async Task Sources_ChapterTitle_ReadsParagraphsByPosition_SkippingPauses_JoiningItems_CountingHeadings()
        {
            await SeedSourceBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Reading(EditTargetSelector.ChapterTitle, "p1 p2 p_end1 part volume", new NodeFilter(1, 1, null)));

            Assert.Equal(
                [("p1", "CHAPTER I.", null), ("p2", "The Period.", null), ("p_end1", "It was the best of times.", null),
                 ("part", "The First Part", null), ("volume", "Book One", null)],
                Read(Assert.Single(targets)));
        }

        [Fact]
        public async Task Sources_ChapterTitle_PastTheEnd_AndBlankLonePart_AreMissing()
        {
            await SeedSourceBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Reading(EditTargetSelector.ChapterTitle, "p2 part"));

            Assert.Equal(4, targets.Count);
            Assert.Equal(("p2", null, "p2 is missing: the chapter has only 1 paragraph"), Read(targets[1])[0]);
            Assert.Equal(("p1", null, "p1 is missing: the chapter has no paragraphs"),
                Read((await _sut.ResolveAsync(_folder, Reading(EditTargetSelector.ChapterTitle, "p1")))[2])[0]);
            Assert.Equal(("part", null, "part is missing: the part title is blank"), Read(targets[3])[1]);
        }

        [Fact]
        public async Task Sources_ParagraphText_PrevNextStopAtTheChaptersEdges_AndOwnParagraphIsSelf()
        {
            await SeedSourceBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Reading(EditTargetSelector.ParagraphText, "prev next p1 p_end1 chapter", new NodeFilter(1, 2, null),
                    Where(new EditPredicate(PredicateField.ItemOrdinal, PredicateOp.Eq, 1))));

            Assert.Equal(4, targets.Count); // three content paragraphs in A, one in the untitled chapter
            Assert.Equal(
                [("prev", null, "prev is missing: this is the chapter's first paragraph"), ("next", "The Period.", null),
                 ("p1", null, "p1 is the paragraph being edited"), ("p_end1", "It was the best of times.", null),
                 ("chapter", "A", null)],
                Read(targets[0]));
            Assert.Equal(("prev", "The Period.", null), Read(targets[2])[0]);
            Assert.Equal(("next", null, "next is missing: this is the chapter's last paragraph"), Read(targets[2])[1]);
            Assert.Equal(("p_end1", null, "p_end1 is the paragraph being edited"), Read(targets[2])[3]);
            Assert.Equal(("chapter", null, "chapter is missing: the chapter title is blank"), Read(targets[3])[4]);
        }

        [Fact]
        public async Task Sources_VolumeTitle_CountsChaptersAcrossTheVolume_AndReadsItsParts()
        {
            await SeedSourceBookAsync();
            var targets = await _sut.ResolveAsync(_folder,
                Reading(EditTargetSelector.VolumeTitle, "ch1 ch2 ch3 ch4 ch_end1 pt2 pt3 pt_end1"));

            Assert.Equal(
                [("ch1", "A", null), ("ch2", null, "ch2 is missing: that chapter title is blank"), ("ch3", "C", null),
                 ("ch4", null, "ch4 is missing: the volume has only 3 chapters"), ("ch_end1", "C", null),
                 ("pt2", "The Second Part", null), ("pt3", null, "pt3 is missing: the volume has only 2 parts"),
                 ("pt_end1", "The Second Part", null)],
                Read(targets[0]));
            Assert.Equal(("pt2", null, "pt2 is missing: the volume has only 1 part"), Read(targets[1])[5]);
            Assert.Equal(("pt_end1", null, "pt_end1 is missing: that part title is blank"), Read(targets[1])[7]);
        }

        [Fact]
        public async Task Sources_PartTitle_ReadsItsOwnChapters_AndItsVolume()
        {
            await SeedSourceBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Reading(EditTargetSelector.PartTitle, "ch1 ch2 ch_end1 volume"));

            Assert.Equal(3, targets.Count);
            Assert.Equal(
                [("ch1", "A", null), ("ch2", null, "ch2 is missing: that chapter title is blank"),
                 ("ch_end1", null, "ch_end1 is missing: that chapter title is blank"), ("volume", "Book One", null)],
                Read(targets[0]));
            Assert.Equal(("ch2", null, "ch2 is missing: the part has only 1 chapter"), Read(targets[1])[1]);
        }

        [Fact]
        public async Task Sources_PlanWithoutSources_ReadsNone()
        {
            await SeedSourceBookAsync();
            var targets = await _sut.ResolveAsync(_folder, Program(EditTargetSelector.ParagraphText));

            Assert.All(targets, t => Assert.Empty(t.Sources));
        }

        private async Task SeedAllCapsTitlesAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("BOOK ONE", v => v
                    .AddPart("PART I", p => p
                        .AddChapter("THE FIVE ORANGE PIPS", c => c.AddParagraph("p1", pp => pp.AddNarration("i1", "Text.")))
                        .AddChapter("Ordinary", c => c.AddParagraph("p2", pp => pp.AddNarration("i2", "Text."))))
                    .AddPart("Part Two", p => p
                        .AddChapter("ANOTHER", c => c.AddParagraph("p3", pp => pp.AddNarration("i3", "Text.")))))
                .BuildAsync();
        }

        [Fact]
        public async Task CountTitlesAlsoMatching_ParagraphChangeCase_CountsEachTitleLevelThePatternMatches()
        {
            await SeedAllCapsTitlesAsync();
            var counts = await _sut.CountTitlesAlsoMatchingAsync(_folder,
                Pattern(EditTargetSelector.ParagraphText, TransformKind.ChangeCase, @"\b\p{Lu}{2,}\b"));

            Assert.Equal(new TitleMatchCounts(Volume: 1, Part: 1, Chapter: 2), counts);
        }

        [Theory]
        [InlineData(EditTargetSelector.PartTitle, TransformKind.ChangeCase, @"\b\p{Lu}{2,}\b")]
        [InlineData(EditTargetSelector.ParagraphText, TransformKind.ChangeCase, null)]
        [InlineData(EditTargetSelector.ParagraphText, TransformKind.RegexReplace, @"\b\p{Lu}{2,}\b")]
        public async Task CountTitlesAlsoMatching_OtherPrograms_CountNothing(
            EditTargetSelector target, TransformKind kind, string? pattern)
        {
            await SeedAllCapsTitlesAsync();
            var counts = await _sut.CountTitlesAlsoMatchingAsync(_folder, Pattern(target, kind, pattern));

            Assert.Equal(0, counts.Total);
        }
    }
}
