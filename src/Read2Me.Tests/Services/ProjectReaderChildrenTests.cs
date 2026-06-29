using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectReaderChildrenTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        public ProjectReaderChildrenTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<(Guid VolumeId, Guid[] PartIds)> SeedVolumeWithPartsAsync(int partCount = 2)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            var partNames = Enumerable.Range(0, partCount).Select(i => $"p{i}").ToArray();
            var scope = b.AddVolume("vol", v =>
            {
                foreach (var name in partNames)
                    v.AddPart(name);
            });
            await scope.BuildAsync();
            return (b.VolumeId("vol"), partNames.Select(n => b.PartId(n)).ToArray());
        }

        private async Task<(Guid PartId, Guid[] ChapterIds)> SeedPartWithChaptersAsync(int chapterCount = 2)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            var chNames = Enumerable.Range(0, chapterCount).Select(i => $"ch{i}").ToArray();
            await b.AddVolume("vol", v => v.AddPart("part", p =>
            {
                foreach (var name in chNames)
                    p.AddChapter(name);
            })).BuildAsync();
            return (b.PartId("part"), chNames.Select(n => b.ChapterId(n)).ToArray());
        }

        private async Task<(Guid ChapterId, Guid[] ParagraphIds)> SeedChapterWithParagraphsAsync(int paragraphCount = 2)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            var paraNames = Enumerable.Range(0, paragraphCount).Select(i => $"para{i}").ToArray();
            await b.AddVolume("vol", v => v.AddChapter("ch", c =>
            {
                foreach (var name in paraNames)
                    c.AddParagraph(name);
            })).BuildAsync();
            return (b.ChapterId("ch"), paraNames.Select(n => b.ParagraphId(n)).ToArray());
        }

        [Fact]
        public async Task GetChildren_Volume_ReturnsOrderedParts()
        {
            var (volId, partIds) = await SeedVolumeWithPartsAsync(3);

            var result = await _reader.GetChildrenAsync(_folder, BookNodeLevel.Volume, volId);

            Assert.NotNull(result.Parts);
            Assert.Null(result.Chapters);
            Assert.Null(result.Paragraphs);
            Assert.Equal(3, result.Parts.Count);
            Assert.Equal(partIds, result.Parts.Select(p => p.Id).ToArray());
        }

        [Fact]
        public async Task GetChildren_Part_ReturnsOrderedChapters()
        {
            var (partId, chapterIds) = await SeedPartWithChaptersAsync(3);

            var result = await _reader.GetChildrenAsync(_folder, BookNodeLevel.Part, partId);

            Assert.Null(result.Parts);
            Assert.NotNull(result.Chapters);
            Assert.Null(result.Paragraphs);
            Assert.Equal(3, result.Chapters.Count);
            Assert.Equal(chapterIds, result.Chapters.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task GetChildren_Chapter_ReturnsOrderedParagraphs()
        {
            var (chapterId, paraIds) = await SeedChapterWithParagraphsAsync(3);

            var result = await _reader.GetChildrenAsync(_folder, BookNodeLevel.Chapter, chapterId);

            Assert.Null(result.Parts);
            Assert.Null(result.Chapters);
            Assert.NotNull(result.Paragraphs);
            Assert.Equal(3, result.Paragraphs.Count);
            Assert.Equal(paraIds, result.Paragraphs.Select(p => p.Id).ToArray());
        }

        [Fact]
        public async Task GetChildren_OrdersByOrderKey()
        {
            // Seed parts out of insertion order: partB has earlier fractional key than partA.
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddPart("partA")
                .AddPart("partB"))
                .BuildAsync();

            // After build, swap the order keys in DB so partB sorts before partA
            var orderFirst = Key();
            var orderSecond = Key(orderFirst);
            await using (var db = await OpenDbAsync())
            {
                var a = await db.Parts.FindAsync(b.PartId("partA"));
                var bPart = await db.Parts.FindAsync(b.PartId("partB"));
                a!.Order = orderSecond;
                bPart!.Order = orderFirst;
                await db.SaveChangesAsync();
            }

            var result = await _reader.GetChildrenAsync(_folder, BookNodeLevel.Volume, b.VolumeId("vol"));

            Assert.NotNull(result.Parts);
            Assert.Equal(2, result.Parts.Count);
            Assert.Equal(b.PartId("partB"), result.Parts[0].Id);
            Assert.Equal(b.PartId("partA"), result.Parts[1].Id);
        }

        [Fact]
        public async Task GetChildren_UnknownParent_ReturnsEmpty()
        {
            // ensure DB exists and is migrated
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

            var result = await _reader.GetChildrenAsync(_folder, BookNodeLevel.Volume, Guid.NewGuid());

            Assert.NotNull(result.Parts);
            Assert.Empty(result.Parts);
        }
    }
}
