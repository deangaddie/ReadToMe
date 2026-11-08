using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Services.Llm;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class ChapterOutlineBuilderTests : ProjectDbTestBase
    {
        private readonly ChapterOutlineBuilder _sut;
        private readonly ProjectFolderId _folder;

        public ChapterOutlineBuilderTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _sut = new ChapterOutlineBuilder(new ProjectReader(session, NullLogger<ProjectReader>.Instance));
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

        [Fact]
        public async Task EditOutline_ListsVolumePartChapterWithSamples()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("Pride and Prejudice", v => v
                    .AddPart("Book One", p => p
                        .AddChapter("Chapter 1", c => c
                            .AddParagraph(null, x => x.AddNarration("a", "1"))
                            .AddParagraph(null, x => x.AddNarration("b", "The Beginning.")))
                        .AddChapter(null, c => { })))
                .BuildAsync();

            var outline = await _sut.BuildForEditAsync(_folder, CancellationToken.None);

            Assert.Equal(Lines(
                "1 volume(s), 1 part(s), 2 chapter(s).",
                "Volume 1: Pride and Prejudice",
                "  Part 1: Book One",
                "    Chapter 1: Chapter 1",
                "      p1: 1",
                "      p2: The Beginning.",
                "    Chapter 2: (untitled)",
                "      (no paragraphs)"), outline);
        }

        [Fact]
        public async Task EditOutline_BlankVolumeAndPartTitlesAreUntitled()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume(" ", v => v
                    .AddChapter("C", c => c.AddParagraph(null, x => x.AddNarration("a", "Text"))))
                .BuildAsync();

            var outline = await _sut.BuildForEditAsync(_folder, CancellationToken.None);

            Assert.Contains("Volume 1: (untitled)", outline);
            Assert.Contains("  Part 1: (untitled)", outline);
        }

        [Fact]
        public async Task EditOutline_SamplesFiveContentParagraphsSkippingPauses()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("V", v => v
                    .AddChapter("C", c => c
                        .AddParagraph(null, x => x.AddPause("z0"))
                        .AddParagraph(null, x => x.AddNarration("a", "one"))
                        .AddParagraph(null, x => x.AddPause("z1"))
                        .AddParagraph(null, x => x.AddNarration("b", "two"))
                        .AddParagraph(null, x => x.AddNarration("c", "three"))
                        .AddParagraph(null, x => x.AddNarration("d", "four"))
                        .AddParagraph(null, x => x.AddNarration("e", "five"))
                        .AddParagraph(null, x => x.AddNarration("f", "six"))))
                .BuildAsync();

            var outline = await _sut.BuildForEditAsync(_folder, CancellationToken.None);

            Assert.Contains("p1: one", outline);
            Assert.Contains("p2: two", outline);
            Assert.Contains("p5: five", outline);
            Assert.DoesNotContain("p6", outline);
            Assert.DoesNotContain("six", outline);
            Assert.DoesNotContain("(no paragraphs)", outline);
        }

        [Fact]
        public async Task EditOutline_CutsLongValuesAtEightyCharsWithEllipsis()
        {
            var exactly80 = new string('a', 80);
            var long81 = new string('b', 81);
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("V", v => v
                    .AddChapter("C", c => c
                        .AddParagraph(null, x => x.AddNarration("a", exactly80))
                        .AddParagraph(null, x => x.AddNarration("b", long81))))
                .BuildAsync();

            var outline = await _sut.BuildForEditAsync(_folder, CancellationToken.None);

            Assert.Contains($"p1: {exactly80}{Environment.NewLine}", outline);
            Assert.Contains($"p2: {new string('b', 80)}…{Environment.NewLine}", outline);
        }

        [Fact]
        public async Task EditOutline_JoinsTextItemsAndTrims()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("V", v => v
                    .AddChapter("C", c => c
                        .AddParagraph(null, x => x
                            .AddNarration("a", "  \"Well,\"")
                            .AddNarration("b", "said Jo.  "))))
                .BuildAsync();

            var outline = await _sut.BuildForEditAsync(_folder, CancellationToken.None);

            Assert.Contains("p1: \"Well,\" said Jo." + Environment.NewLine, outline);
        }

        [Fact]
        public async Task EditOutline_KeepsEachSampleOnOneLine()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("V", v => v
                    .AddChapter("C", c => c
                        .AddParagraph(null, x => x.AddNarration("a", "line one\n  line two"))))
                .BuildAsync();

            var outline = await _sut.BuildForEditAsync(_folder, CancellationToken.None);

            Assert.Contains("p1: line one line two" + Environment.NewLine, outline);
        }

        [Fact]
        public async Task EditOutline_CapsAtTwentyChaptersWithMoreLine()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("V", v =>
                {
                    for (var i = 1; i <= 25; i++)
                        v.AddChapter($"ch{i}", c => c.AddParagraph(null, x => x.AddNarration($"n{i}", $"text {i}")));
                })
                .BuildAsync();

            var outline = await _sut.BuildForEditAsync(_folder, CancellationToken.None);

            Assert.Contains("Chapter 20: ch20", outline);
            Assert.DoesNotContain("Chapter 21", outline);
            Assert.DoesNotContain("text 21", outline);
            Assert.EndsWith($"... and 5 more chapters.{Environment.NewLine}", outline);
        }

        [Fact]
        public async Task DiscoveryOutline_StaysTitlesOnly()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("Vol", v => v
                    .AddPart("Part", p => p
                        .AddChapter("One", c => c.AddParagraph(null, x => x.AddNarration("a", "Text")))
                        .AddChapter(null, c => { })))
                .BuildAsync();

            var outline = await _sut.BuildAsync(_folder, CancellationToken.None);

            Assert.Equal(Lines(
                "1 volume(s), 1 part(s), 2 chapter(s).",
                "Chapter 1: One",
                "Chapter 2: (untitled)"), outline);
        }
    }
}
