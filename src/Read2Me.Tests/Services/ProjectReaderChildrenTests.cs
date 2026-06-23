using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
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
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            db.Volumes.Add(vol);

            var partIds = new Guid[partCount];
            string? prev = null;
            for (int i = 0; i < partCount; i++)
            {
                var order = Key(prev); prev = order;
                var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = order };
                db.Parts.Add(part);
                partIds[i] = part.Id;
            }
            await db.SaveChangesAsync();
            return (vol.Id, partIds);
        }

        private async Task<(Guid PartId, Guid[] ChapterIds)> SeedPartWithChaptersAsync(int chapterCount = 2)
        {
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            db.Volumes.Add(vol);
            db.Parts.Add(part);

            var chapterIds = new Guid[chapterCount];
            string? prev = null;
            for (int i = 0; i < chapterCount; i++)
            {
                var order = Key(prev); prev = order;
                var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = order };
                db.Chapters.Add(ch);
                chapterIds[i] = ch.Id;
            }
            await db.SaveChangesAsync();
            return (part.Id, chapterIds);
        }

        private async Task<(Guid ChapterId, Guid[] ParagraphIds)> SeedChapterWithParagraphsAsync(int paragraphCount = 2)
        {
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);

            var paraIds = new Guid[paragraphCount];
            string? prev = null;
            for (int i = 0; i < paragraphCount; i++)
            {
                var order = Key(prev); prev = order;
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = order };
                db.Paragraphs.Add(para);
                paraIds[i] = para.Id;
            }
            await db.SaveChangesAsync();
            return (ch.Id, paraIds);
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
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            db.Volumes.Add(vol);

            var orderFirst = Key();          // e.g. "a0"
            var orderSecond = Key(orderFirst); // e.g. "a1"

            // Insert partA with the later order key first, then partB with the earlier key.
            var partA = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = orderSecond };
            var partB = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = orderFirst };
            db.Parts.Add(partA); // DB insertion order: first, but has later fractional key
            db.Parts.Add(partB); // DB insertion order: second, but has earlier fractional key
            await db.SaveChangesAsync();

            var result = await _reader.GetChildrenAsync(_folder, BookNodeLevel.Volume, vol.Id);

            Assert.NotNull(result.Parts);
            Assert.Equal(2, result.Parts.Count);
            // partB (orderFirst) should sort before partA (orderSecond).
            Assert.Equal(partB.Id, result.Parts[0].Id);
            Assert.Equal(partA.Id, result.Parts[1].Id);
        }

        [Fact]
        public async Task GetChildren_UnknownParent_ReturnsEmpty()
        {
            await using var db = await OpenDbAsync(); // ensure DB exists and is migrated

            var result = await _reader.GetChildrenAsync(_folder, BookNodeLevel.Volume, Guid.NewGuid());

            Assert.NotNull(result.Parts);
            Assert.Empty(result.Parts);
        }
    }
}
