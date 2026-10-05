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

        private async Task SeedAllCapsTitlesAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("BOOK ONE", v => v
                    .AddPart("PART I", p => p
                        .AddChapter("THE MULE", c => c.AddParagraph("p1", pp => pp.AddNarration("i1", "Text.")))
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
