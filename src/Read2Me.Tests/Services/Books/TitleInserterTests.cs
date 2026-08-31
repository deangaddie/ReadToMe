using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class TitleInserterTests : IDisposable
    {
        private readonly string _tempDir;

        public TitleInserterTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "TitleInserterTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private async Task<ProjectDbContext> OpenDbAsync()
        {
            var dbPath = Path.Combine(_tempDir, "project.db");
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }

        // Seeds the minimum chain (Volume→Part→Chapter) and returns the chapter id.
        private static async Task<Guid> SeedChapterAsync(ProjectDbContext db)
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = OrderKeyGenerator.GenerateKeyBetween(null, null) };
            var chapter = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = OrderKeyGenerator.GenerateKeyBetween(null, null) };
            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(chapter);
            await db.SaveChangesAsync();
            return chapter.Id;
        }

        [Fact]
        public async Task AddTitleParagraph_OrdersBeforeExistingFirst()
        {
            await using var db = await OpenDbAsync();
            var chapterId = await SeedChapterAsync(db);
            var existingOrder = OrderKeyGenerator.GenerateKeyBetween(null, null);

            var result = TitleInserter.AddTitleParagraph(db, chapterId, "Title", existingOrder);
            await db.SaveChangesAsync();

            Assert.True(string.Compare(result.Order, existingOrder, StringComparison.Ordinal) < 0);
        }

        [Fact]
        public async Task AddTitleParagraph_CreatesSingleNarrationItem()
        {
            await using var db = await OpenDbAsync();
            var chapterId = await SeedChapterAsync(db);

            var result = TitleInserter.AddTitleParagraph(db, chapterId, "Hello", null);
            await db.SaveChangesAsync();

            var items = await db.ParagraphItems.Where(i => i.ParagraphId == result.Id).ToListAsync();
            Assert.Single(items);
            Assert.Equal(ParagraphItemType.Narration, items[0].ItemType);
            Assert.Equal("Hello", items[0].Text);
        }

        // An inserted title is narration, so it carries the narrator like any other narration item —
        // otherwise it would need backfilling again the moment it is inserted.
        [Fact]
        public async Task AddTitleParagraph_StampsTheNarrator()
        {
            await using var db = await OpenDbAsync();
            var chapterId = await SeedChapterAsync(db);

            var result = TitleInserter.AddTitleParagraph(db, chapterId, "Chapter One", null);
            await db.SaveChangesAsync();

            var item = await db.ParagraphItems.SingleAsync(i => i.ParagraphId == result.Id);
            Assert.Equal(ProjectDbContext.NarratorId, item.CharacterId);
        }

        [Fact]
        public async Task AddTitleParagraphAfter_StampsTheNarrator()
        {
            await using var db = await OpenDbAsync();
            var chapterId = await SeedChapterAsync(db);
            var first = TitleInserter.AddTitleParagraph(db, chapterId, "First", null);
            await db.SaveChangesAsync();

            var second = TitleInserter.AddTitleParagraphAfter(db, chapterId, "Second", first.Order);
            await db.SaveChangesAsync();

            var item = await db.ParagraphItems.SingleAsync(i => i.ParagraphId == second.Id);
            Assert.Equal(ProjectDbContext.NarratorId, item.CharacterId);
        }

        [Fact]
        public async Task AddTitleParagraph_WhenChapterEmpty_StillCreatesParagraph()
        {
            await using var db = await OpenDbAsync();
            var chapterId = await SeedChapterAsync(db);

            var result = TitleInserter.AddTitleParagraph(db, chapterId, "Title", null);
            await db.SaveChangesAsync();

            Assert.Equal(chapterId, result.ChapterId);
            Assert.NotEqual(Guid.Empty, result.Id);
        }

        [Fact]
        public async Task AddTitleParagraphAfter_OrdersAfterGivenOrder()
        {
            await using var db = await OpenDbAsync();
            var chapterId = await SeedChapterAsync(db);
            var first = TitleInserter.AddTitleParagraph(db, chapterId, "First", null);
            await db.SaveChangesAsync();

            var second = TitleInserter.AddTitleParagraphAfter(db, chapterId, "Second", first.Order);
            await db.SaveChangesAsync();

            Assert.True(string.Compare(second.Order, first.Order, StringComparison.Ordinal) > 0);
        }
    }
}

