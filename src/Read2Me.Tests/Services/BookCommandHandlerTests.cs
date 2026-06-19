using System;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        // AddPausesCommand
        // ---------------------------------------------------------------

        private async Task<(Chapter ch1, Chapter ch2, Paragraph paraA, Paragraph paraB)> SeedTwoChapterHierarchyAsync(ProjectDbContext db)
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch1 = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch1", Order = Key() };
            var ch2 = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch2", Order = Key(ch1.Order) };

            var paraA = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch1.Id, Order = Key() };
            var paraB = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch1.Id, Order = Key(paraA.Order) };
            var itemA = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = paraA.Id, ItemType = ParagraphItemType.Narration, Text = "A", Order = Key() };
            var itemB = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = paraB.Id, ItemType = ParagraphItemType.Narration, Text = "B", Order = Key() };
            var paraC = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch2.Id, Order = Key() };
            var itemC = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = paraC.Id, ItemType = ParagraphItemType.Narration, Text = "C", Order = Key() };

            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.AddRange(ch1, ch2);
            db.Paragraphs.AddRange(paraA, paraB, paraC);
            db.ParagraphItems.AddRange(itemA, itemB, itemC);
            await db.SaveChangesAsync();
            return (ch1, ch2, paraA, paraB);
        }

        [Fact]
        public async Task AddPausesCommand_InsertsPauseParagraphs()
        {
            await using var db = await SeedProjectAsync();
            await SeedTwoChapterHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new AddPausesCommand(_folder));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.ParagraphItems.AnyAsync(i => i.ItemType == ParagraphItemType.ParagraphPause));
            Assert.True(await verify.ParagraphItems.AnyAsync(i => i.ItemType == ParagraphItemType.ChapterPause));
        }

        [Fact]
        public async Task AddPausesCommand_IsIdempotent()
        {
            await using var db = await SeedProjectAsync();
            await SeedTwoChapterHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new AddPausesCommand(_folder));

            await using var count1db = await OpenDbAsync();
            var countAfterFirst = await count1db.ParagraphItems
                .CountAsync(i => i.ItemType == ParagraphItemType.ParagraphPause || i.ItemType == ParagraphItemType.ChapterPause);
            await count1db.DisposeAsync();

            await _svc.ExecuteAsync(new AddPausesCommand(_folder));

            await using var count2db = await OpenDbAsync();
            var countAfterSecond = await count2db.ParagraphItems
                .CountAsync(i => i.ItemType == ParagraphItemType.ParagraphPause || i.ItemType == ParagraphItemType.ChapterPause);

            Assert.Equal(countAfterFirst, countAfterSecond);
        }

        // ---------------------------------------------------------------
        // InsertPauseParagraphCommand
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(PauseKind.Pause,          ParagraphItemType.Pause)]
        [InlineData(PauseKind.ParagraphPause, ParagraphItemType.ParagraphPause)]
        [InlineData(PauseKind.ChapterPause,   ParagraphItemType.ChapterPause)]
        [InlineData(PauseKind.PartPause,      ParagraphItemType.PartPause)]
        [InlineData(PauseKind.VolumePause,    ParagraphItemType.VolumePause)]
        public async Task InsertPauseParagraphCommand_Before_InsertsCorrectPauseTypeBeforeParagraph(
            PauseKind kind, ParagraphItemType expectedType)
        {
            await using var db = await SeedProjectAsync();
            var (_, _, ch, para, item) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new InsertPauseParagraphCommand(_folder, item.Id, PauseInsertPosition.Before, kind));

            await using var verify = await OpenDbAsync();
            var paragraphs = await verify.Paragraphs
                .Where(p => p.ChapterId == ch.Id)
                .OrderBy(p => p.Order)
                .ToListAsync();
            Assert.Equal(2, paragraphs.Count);
            var pausePara = paragraphs[0];
            Assert.NotEqual(para.Id, pausePara.Id);
            var pauseItem = await verify.ParagraphItems.SingleAsync(i => i.ParagraphId == pausePara.Id);
            Assert.Equal(expectedType, pauseItem.ItemType);
            Assert.Equal(para.Id, paragraphs[1].Id);
        }

        [Fact]
        public async Task InsertPauseParagraphCommand_After_InsertsPauseAfterParagraph()
        {
            await using var db = await SeedProjectAsync();
            var (_, _, ch, para, item) = await SeedHierarchyAsync(db);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new InsertPauseParagraphCommand(_folder, item.Id, PauseInsertPosition.After, PauseKind.ParagraphPause));

            await using var verify = await OpenDbAsync();
            var paragraphs = await verify.Paragraphs
                .Where(p => p.ChapterId == ch.Id)
                .OrderBy(p => p.Order)
                .ToListAsync();
            Assert.Equal(2, paragraphs.Count);
            Assert.Equal(para.Id, paragraphs[0].Id);
            var pauseItem = await verify.ParagraphItems.SingleAsync(i => i.ParagraphId == paragraphs[1].Id);
            Assert.Equal(ParagraphItemType.ParagraphPause, pauseItem.ItemType);
        }

        [Fact]
        public async Task InsertPauseParagraphCommand_Between_InsertsPauseBetweenExistingParagraphs()
        {
            await using var db = await SeedProjectAsync();
            var (_, _, ch, para, item) = await SeedHierarchyAsync(db);
            var para2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(para.Order) };
            var item2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para2.Id, ItemType = ParagraphItemType.Narration, Text = "Second", Order = Key() };
            db.Paragraphs.Add(para2);
            db.ParagraphItems.Add(item2);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new InsertPauseParagraphCommand(_folder, item.Id, PauseInsertPosition.After, PauseKind.ChapterPause));

            await using var verify = await OpenDbAsync();
            var paragraphs = await verify.Paragraphs
                .Where(p => p.ChapterId == ch.Id)
                .OrderBy(p => p.Order)
                .ToListAsync();
            Assert.Equal(3, paragraphs.Count);
            Assert.Equal(para.Id,  paragraphs[0].Id);
            Assert.Equal(para2.Id, paragraphs[2].Id);
            var pauseItem = await verify.ParagraphItems.SingleAsync(i => i.ParagraphId == paragraphs[1].Id);
            Assert.Equal(ParagraphItemType.ChapterPause, pauseItem.ItemType);
            Assert.True(string.Compare(paragraphs[0].Order, paragraphs[1].Order, StringComparison.Ordinal) < 0);
            Assert.True(string.Compare(paragraphs[1].Order, paragraphs[2].Order, StringComparison.Ordinal) < 0);
        }

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

        // ---------------------------------------------------------------
        // CreateCharacterCommand
        // ---------------------------------------------------------------

        [Fact]
        public async Task CreateCharacterCommand_CreatesCharacter()
        {
            await using var db = await SeedProjectAsync();
            await db.DisposeAsync();

            var result = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Mr. Hyde"));

            Assert.NotNull(result);
            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Name == "Mr. Hyde" && c.Id == result.Value));
        }

        [Fact]
        public async Task CreateCharacterCommand_ReusesExistingByName()
        {
            await using var db = await SeedProjectAsync();
            var existing = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Characters.Add(existing);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            var result = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));

            Assert.Equal(existing.Id, result);
            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.Characters.CountAsync(c => c.Name == "Alice"));
        }

        // ---------------------------------------------------------------
        // SetParagraphCharacterCommand
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetParagraphCharacterCommand_AssignsAllCharacterItems()
        {
            await using var db = await SeedProjectAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch", Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var charItem1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character, Text = "Hello", Order = Key() };
            var charItem2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character, Text = "World", Order = Key(charItem1.Order) };
            var narrationItem = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Narration, Text = "Narration", Order = Key(charItem2.Order) };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch); db.Paragraphs.Add(para);
            db.ParagraphItems.AddRange(charItem1, charItem2, narrationItem);
            db.Characters.Add(character);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, para.Id, character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(charItem1.Id))!.CharacterId);
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(charItem2.Id))!.CharacterId);
            Assert.Null((await verify.ParagraphItems.FindAsync(narrationItem.Id))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_WithVoiceInstructions_PersistsOnCharacterItems()
        {
            await using var db = await SeedProjectAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch", Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var charItem = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character, Text = "Hello", Order = Key() };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch); db.Paragraphs.Add(para);
            db.ParagraphItems.Add(charItem);
            db.Characters.Add(character);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, para.Id, character.Id, "whispering, tense"));

            await using var verify = await OpenDbAsync();
            var item = await verify.ParagraphItems.FindAsync(charItem.Id);
            Assert.Equal(character.Id, item!.CharacterId);
            Assert.Equal("whispering, tense", item.VoiceInstructions);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_NullVoiceInstructions_DoesNotClobberExisting()
        {
            await using var db = await SeedProjectAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch", Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var charItem = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character,
                Text = "Hello", Order = Key(), VoiceInstructions = "existing voice"
            };
            var character = new Character { Id = Guid.NewGuid(), Name = "Bob" };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch); db.Paragraphs.Add(para);
            db.ParagraphItems.Add(charItem);
            db.Characters.Add(character);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            // No VoiceInstructions passed (null) — existing value must survive
            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, para.Id, character.Id));

            await using var verify = await OpenDbAsync();
            var item = await verify.ParagraphItems.FindAsync(charItem.Id);
            Assert.Equal(character.Id, item!.CharacterId);
            Assert.Equal("existing voice", item.VoiceInstructions);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_WithId_SetsAllCharacterItemsLeavesNarrationUntouched()
        {
            await using var db = await SeedProjectAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch", Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var ci1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character, Text = "A", Order = Key() };
            var ci2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character, Text = "B", Order = Key(ci1.Order) };
            var ni = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Narration, Text = "N", Order = Key(ci2.Order) };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch); db.Paragraphs.Add(para);
            db.ParagraphItems.AddRange(ci1, ci2, ni);
            db.Characters.Add(character);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, para.Id, character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(ci1.Id))!.CharacterId);
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(ci2.Id))!.CharacterId);
            Assert.Null((await verify.ParagraphItems.FindAsync(ni.Id))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_WithNullId_ClearsAllCharacterItems()
        {
            await using var db = await SeedProjectAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch", Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var existingChar = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var ci1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character, Text = "A", Order = Key(), CharacterId = existingChar.Id };
            var ci2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = ParagraphItemType.Character, Text = "B", Order = Key(ci1.Order), CharacterId = existingChar.Id };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch); db.Paragraphs.Add(para);
            db.Characters.Add(existingChar);
            db.ParagraphItems.AddRange(ci1, ci2);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, para.Id, null));

            await using var verify = await OpenDbAsync();
            Assert.Null((await verify.ParagraphItems.FindAsync(ci1.Id))!.CharacterId);
            Assert.Null((await verify.ParagraphItems.FindAsync(ci2.Id))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_DoesNotTouchOtherParagraphs()
        {
            await using var db = await SeedProjectAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "Part", Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch", Order = Key() };
            var para1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var para2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(para1.Order) };
            var target = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para1.Id, ItemType = ParagraphItemType.Character, Text = "T", Order = Key() };
            var other = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para2.Id, ItemType = ParagraphItemType.Character, Text = "O", Order = Key() };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch); db.Paragraphs.AddRange(para1, para2);
            db.ParagraphItems.AddRange(target, other);
            db.Characters.Add(character);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, para1.Id, character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(target.Id))!.CharacterId);
            Assert.Null((await verify.ParagraphItems.FindAsync(other.Id))!.CharacterId);
        }

    }
}
