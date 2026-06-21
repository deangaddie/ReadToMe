using System;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;
using EntityState = Read2Me.Data.Enums.AudioReviewState;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioReviewHandlerTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public AudioReviewHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            _svc = sp.GetRequiredService<BookCommandHandler>();
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<Guid> SeedItemAsync()
        {
            await using var db = await OpenDbAsync();
            db.Projects.Add(new Project { Title = "T", BookTitle = "B", Author = "A", Filename = "t.epub", Type = BookFileType.Epub });
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch", Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var item = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Narration, Text = "Hello", Order = Key() };
            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        }

        private SetAudioReviewCommand SetFailure(Guid itemId) =>
            new(_folder, itemId,
                NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.42, VerifyReason: "over threshold",
                Transcript: "got this", OriginalTextSnapshot: "Hello");

        private SetAudioReviewCommand SetBothOk(Guid itemId) =>
            new(_folder, itemId,
                NormalizeOk: true, NormalizeReason: null,
                VerifyOk: true, Wer: 0.01, VerifyReason: null,
                Transcript: "Hello", OriginalTextSnapshot: "Hello");

        [Fact]
        public async Task SetWithFailure_CreatesNeedsReviewRow()
        {
            var itemId = await SeedItemAsync();

            await _svc.ExecuteAsync(SetFailure(itemId));

            await using var verify = await OpenDbAsync();
            var row = await verify.AudioReviews.AsNoTracking().SingleAsync(r => r.ParagraphItemId == itemId);
            Assert.Equal(EntityState.NeedsReview, row.State);
            Assert.False(row.VerifyOk);
            Assert.Equal(0.42, row.Wer);
            Assert.Equal("over threshold", row.VerifyReason);
        }

        [Fact]
        public async Task SetWithFailure_Twice_UpdatesSameRow_NoDuplicate()
        {
            var itemId = await SeedItemAsync();

            await _svc.ExecuteAsync(SetFailure(itemId));
            await _svc.ExecuteAsync(new SetAudioReviewCommand(_folder, itemId,
                NormalizeOk: false, NormalizeReason: "clipping",
                VerifyOk: false, Wer: 0.5, VerifyReason: "worse",
                Transcript: "x", OriginalTextSnapshot: "Hello"));

            await using var verify = await OpenDbAsync();
            var rows = await verify.AudioReviews.AsNoTracking().Where(r => r.ParagraphItemId == itemId).ToListAsync();
            Assert.Single(rows);
            Assert.Equal("clipping", rows[0].NormalizeReason);
            Assert.Equal(0.5, rows[0].Wer);
        }

        [Fact]
        public async Task SetWithBothOk_RemovesRow()
        {
            var itemId = await SeedItemAsync();
            await _svc.ExecuteAsync(SetFailure(itemId));

            await _svc.ExecuteAsync(SetBothOk(itemId));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.AudioReviews.AsNoTracking().AnyAsync(r => r.ParagraphItemId == itemId));
        }

        [Fact]
        public async Task SetWithBothOk_OnMissingRow_IsNoOp()
        {
            var itemId = await SeedItemAsync();

            await _svc.ExecuteAsync(SetBothOk(itemId));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.AudioReviews.AsNoTracking().AnyAsync(r => r.ParagraphItemId == itemId));
        }

        [Fact]
        public async Task SetWithFailure_AfterDismiss_ResetsToNeedsReview()
        {
            var itemId = await SeedItemAsync();
            await _svc.ExecuteAsync(SetFailure(itemId));
            await _svc.ExecuteAsync(new DismissAudioReviewCommand(_folder, itemId));

            await _svc.ExecuteAsync(SetFailure(itemId));

            await using var verify = await OpenDbAsync();
            var row = await verify.AudioReviews.AsNoTracking().SingleAsync(r => r.ParagraphItemId == itemId);
            Assert.Equal(EntityState.NeedsReview, row.State);
        }

        [Fact]
        public async Task Dismiss_FlipsExistingRowToDismissed()
        {
            var itemId = await SeedItemAsync();
            await _svc.ExecuteAsync(SetFailure(itemId));

            await _svc.ExecuteAsync(new DismissAudioReviewCommand(_folder, itemId));

            await using var verify = await OpenDbAsync();
            var row = await verify.AudioReviews.AsNoTracking().SingleAsync(r => r.ParagraphItemId == itemId);
            Assert.Equal(EntityState.Dismissed, row.State);
        }

        [Fact]
        public async Task Dismiss_OnMissingRow_IsNoOp()
        {
            var itemId = await SeedItemAsync();

            var result = await _svc.ExecuteAsync(new DismissAudioReviewCommand(_folder, itemId));

            Assert.Null(result);
            await using var verify = await OpenDbAsync();
            Assert.False(await verify.AudioReviews.AsNoTracking().AnyAsync(r => r.ParagraphItemId == itemId));
        }
    }
}
