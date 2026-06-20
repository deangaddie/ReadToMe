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

namespace Read2Me.Tests.Services.Audio
{
    public class SetParagraphItemAudioHandlerTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public SetParagraphItemAudioHandlerTests()
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

        [Fact]
        public async Task SetParagraphItemAudio_SetsAudioFileName()
        {
            var itemId = await SeedItemAsync();

            await _svc.ExecuteAsync(new SetParagraphItemAudioCommand(_folder, itemId, "audio/abc.wav"));

            await using var verify = await OpenDbAsync();
            var updated = await verify.ParagraphItems
                .AsNoTracking()
                .FirstAsync(i => i.Id == itemId);
            Assert.Equal("audio/abc.wav", updated.AudioFileName);
        }

        [Fact]
        public async Task SetParagraphItemAudio_UnknownItem_ReturnsNull()
        {
            await using var _ = await OpenDbAsync();
            var result = await _svc.ExecuteAsync(new SetParagraphItemAudioCommand(_folder, Guid.NewGuid(), "audio/x.wav"));
            Assert.Null(result);
        }
    }
}
