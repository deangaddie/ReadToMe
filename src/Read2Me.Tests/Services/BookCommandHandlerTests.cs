using System;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Books;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class BookCommandHandlerTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public BookCommandHandlerTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _svc = new BookCommandHandler(session);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<ProjectDbContext> SeedProjectAsync()
        {
            var db = await OpenDbAsync();
            db.Projects.Add(new Project { Title = "Test Book", BookTitle = "The Book", Author = "Author", Filename = "test.epub", Type = BookFileType.Epub });
            await db.SaveChangesAsync();
            return db;
        }

        private async Task<(Volume vol, Part part, Chapter ch, Paragraph para, ParagraphItem item)> SeedHierarchyAsync(ProjectDbContext db)
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol 1", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part 1", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Chapter 1", Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var item = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Narration, Text = "Hello world", Order = Key() };

            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();
            return (vol, part, ch, para, item);
        }

        // ---------------------------------------------------------------
        // Delete commands
        // ---------------------------------------------------------------

        [Fact]
        public async Task DeleteVolumeCommand_RemovesVolume()
        {
            await using var db = await SeedProjectAsync();
            var (vol, _, _, _, _) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new DeleteVolumeCommand(_folder, vol.Id));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Volumes.AnyAsync(v => v.Id == vol.Id));
        }

        [Fact]
        public async Task DeletePartCommand_RemovesPart()
        {
            await using var db = await SeedProjectAsync();
            var (_, part, _, _, _) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new DeletePartCommand(_folder, part.Id));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Parts.AnyAsync(p => p.Id == part.Id));
        }

        [Fact]
        public async Task DeleteChapterCommand_RemovesChapter()
        {
            await using var db = await SeedProjectAsync();
            var (_, _, ch, _, _) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new DeleteChapterCommand(_folder, ch.Id));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Chapters.AnyAsync(c => c.Id == ch.Id));
        }

        [Fact]
        public async Task DeleteParagraphCommand_RemovesParagraph()
        {
            await using var db = await SeedProjectAsync();
            var (_, _, _, para, _) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new DeleteParagraphCommand(_folder, para.Id));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Paragraphs.AnyAsync(p => p.Id == para.Id));
        }

        [Fact]
        public async Task DeleteParagraphItemCommand_RemovesItem()
        {
            await using var db = await SeedProjectAsync();
            var (_, _, _, _, item) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new DeleteParagraphItemCommand(_folder, item.Id));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.ParagraphItems.AnyAsync(i => i.Id == item.Id));
        }

        // ---------------------------------------------------------------
        // Update commands
        // ---------------------------------------------------------------

        [Fact]
        public async Task UpdateVolumeTitleCommand_UpdatesTitle()
        {
            await using var db = await SeedProjectAsync();
            var (vol, _, _, _, _) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new UpdateVolumeTitleCommand(_folder, vol.Id, "New Volume Title"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("New Volume Title", (await verify.Volumes.FindAsync(vol.Id))!.Title);
        }

        [Fact]
        public async Task UpdateParagraphItemTextCommand_UpdatesText()
        {
            await using var db = await SeedProjectAsync();
            var (_, _, _, _, item) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new UpdateParagraphItemTextCommand(_folder, item.Id, "Updated text"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Updated text", (await verify.ParagraphItems.FindAsync(item.Id))!.Text);
        }

        // ---------------------------------------------------------------
        // Merge commands
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeVolumeCommand_Previous_MergesVolume()
        {
            await using var db = await SeedProjectAsync();

            var vol1 = new Volume { Id = Guid.NewGuid(), Title = "Vol 1", Order = Key() };
            var vol2 = new Volume { Id = Guid.NewGuid(), Title = "Vol 2", Order = Key(vol1.Order) };
            var part2 = new Part { Id = Guid.NewGuid(), VolumeId = vol2.Id, Order = Key() };
            db.Volumes.AddRange(vol1, vol2);
            db.Parts.Add(part2);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new MergeVolumeCommand(_folder, vol2.Id, MergeDirection.Previous));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Volumes.AnyAsync(v => v.Id == vol2.Id));
            Assert.True(await verify.Parts.AnyAsync(p => p.VolumeId == vol1.Id));
        }

        [Fact]
        public async Task MergeVolumeCommand_Previous_FirstVolume_NoOp()
        {
            await using var db = await SeedProjectAsync();
            var (vol, _, _, _, _) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new MergeVolumeCommand(_folder, vol.Id, MergeDirection.Previous));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Volumes.AnyAsync(v => v.Id == vol.Id));
        }

        [Fact]
        public async Task MergeVolumeCommand_Next_LastVolume_NoOp()
        {
            await using var db = await SeedProjectAsync();
            var (vol, _, _, _, _) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new MergeVolumeCommand(_folder, vol.Id, MergeDirection.Next));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Volumes.AnyAsync(v => v.Id == vol.Id));
        }

        // ---------------------------------------------------------------
        // SetItemCharacterCommand
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetItemCharacterCommand_AssignsCharacter()
        {
            await using var db = await SeedProjectAsync();
            var (_, _, _, _, item) = await SeedHierarchyAsync(db);
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", IsNarrator = false };
            db.Characters.Add(character);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, item.Id, character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(item.Id))!.CharacterId);
        }

        // ---------------------------------------------------------------
        // ClearBookContentCommand
        // ---------------------------------------------------------------

        [Fact]
        public async Task ClearBookContentCommand_RemovesAllHierarchy()
        {
            await using var db = await SeedProjectAsync();
            await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new ClearBookContentCommand(_folder));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Volumes.AnyAsync());
            Assert.False(await verify.Parts.AnyAsync());
            Assert.False(await verify.Chapters.AnyAsync());
            Assert.False(await verify.Paragraphs.AnyAsync());
            Assert.False(await verify.ParagraphItems.AnyAsync());
        }

        // ---------------------------------------------------------------
        // Unknown command type
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_UnknownCommand_ThrowsNotSupportedException()
        {
            var unknownCmd = new UnknownTestCommand(_folder);
            await Assert.ThrowsAsync<NotSupportedException>(() => _svc.ExecuteAsync(unknownCmd));
        }

        private record UnknownTestCommand(ProjectFolderId FolderId) : BookCommand(FolderId);

        // ---------------------------------------------------------------
        // ApplyMutationAsync — ToUpdate with detached entity
        // ---------------------------------------------------------------

        [Fact]
        public async Task ApplyMutation_WithDetachedUpdatedEntity_PersistsFkChange()
        {
            await using var seed = await SeedProjectAsync();
            var vol1 = new Volume { Id = Guid.NewGuid(), Title = "Vol 1", Order = Key() };
            var vol2 = new Volume { Id = Guid.NewGuid(), Title = "Vol 2", Order = Key(vol1.Order) };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol1.Id, Order = Key() };
            seed.Volumes.AddRange(vol1, vol2);
            seed.Parts.Add(part);
            await seed.SaveChangesAsync();
            await seed.DisposeAsync();

            await using var db = await OpenDbAsync();
            var trackedPart = await db.Parts.FindAsync(part.Id);
            db.Entry(trackedPart!).State = EntityState.Detached;

            // Mutate FK in memory on the detached entity
            trackedPart!.VolumeId = vol2.Id;

            var mutation = new HierarchyMutation(ToAdd: [], ToDelete: [], ToUpdate: [trackedPart]);
            await BookCommandHandler.ApplyMutationAsync(db, mutation);
            await db.DisposeAsync();

            await using var verify = await OpenDbAsync();
            var saved = await verify.Parts.FindAsync(part.Id);
            Assert.Equal(vol2.Id, saved!.VolumeId);
        }

    }
}
