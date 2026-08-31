using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Books;
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

        // ---------------------------------------------------------------
        // Delete commands
        // ---------------------------------------------------------------

        [Fact]
        public async Task DeleteVolumeCommand_RemovesVolume()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new DeleteVolumeCommand(_folder, b.VolumeId("vol")));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Volumes.AnyAsync(v => v.Id == b.VolumeId("vol")));
        }

        [Fact]
        public async Task DeletePartCommand_RemovesPart()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddPart("part", p => p.AddChapter(configure: c => c.AddParagraph(configure: p2 => p2.AddNarration("item", "Hello world"))))).BuildAsync();

            await _svc.ExecuteAsync(new DeletePartCommand(_folder, b.PartId("part")));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Parts.AnyAsync(p => p.Id == b.PartId("part")));
        }

        [Fact]
        public async Task DeleteChapterCommand_RemovesChapter()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new DeleteChapterCommand(_folder, b.ChapterId("ch")));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Chapters.AnyAsync(c => c.Id == b.ChapterId("ch")));
        }

        [Fact]
        public async Task DeleteParagraphCommand_RemovesParagraph()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph("para", p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new DeleteParagraphCommand(_folder, b.ParagraphId("para")));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Paragraphs.AnyAsync(p => p.Id == b.ParagraphId("para")));
        }

        [Fact]
        public async Task DeleteParagraphItemCommand_RemovesItem()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new DeleteParagraphItemCommand(_folder, b.ItemId("item")));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.ParagraphItems.AnyAsync(i => i.Id == b.ItemId("item")));
        }

        // ---------------------------------------------------------------
        // Update commands
        // ---------------------------------------------------------------

        [Fact]
        public async Task UpdateVolumeTitleCommand_UpdatesTitle()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new UpdateVolumeTitleCommand(_folder, b.VolumeId("vol"), "New Volume Title"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("New Volume Title", (await verify.Volumes.FindAsync(b.VolumeId("vol")))!.Title);
        }

        [Fact]
        public async Task UpdateParagraphItemTextCommand_UpdatesText()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new UpdateParagraphItemTextCommand(_folder, b.ItemId("item"), "Updated text"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Updated text", (await verify.ParagraphItems.FindAsync(b.ItemId("item")))!.Text);
        }

        // ---------------------------------------------------------------
        // Merge commands
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeVolumeCommand_Previous_MergesVolume()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b
                .AddVolume("vol1")
                .AddVolume("vol2", v => v.AddPart("part2"))
                .BuildAsync();

            await _svc.ExecuteAsync(new MergeVolumeCommand(_folder, b.VolumeId("vol2"), MergeDirection.Previous));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Volumes.AnyAsync(v => v.Id == b.VolumeId("vol2")));
            Assert.True(await verify.Parts.AnyAsync(p => p.VolumeId == b.VolumeId("vol1")));
        }

        [Fact]
        public async Task MergeVolumeCommand_Previous_FirstVolume_NoOp()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new MergeVolumeCommand(_folder, b.VolumeId("vol"), MergeDirection.Previous));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Volumes.AnyAsync(v => v.Id == b.VolumeId("vol")));
        }

        [Fact]
        public async Task MergeVolumeCommand_Next_LastVolume_NoOp()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new MergeVolumeCommand(_folder, b.VolumeId("vol"), MergeDirection.Next));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Volumes.AnyAsync(v => v.Id == b.VolumeId("vol")));
        }

        // ---------------------------------------------------------------
        // SetItemCharacterCommand
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetItemCharacterCommand_AssignsCharacter()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", IsNarrator = false };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p =>
                p.AddRawItem("item", ParagraphItemType.Character, "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, b.ItemId("item"), character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("item")))!.CharacterId);
        }

        [Fact]
        public async Task SetItemCharacterCommand_AssignsCharacterToNarrationItem()
        {
            // Narration is a speaker, not an item type (ADR-0006): a line the splitter misread as
            // narration is repaired by stamping the character, and the voice resolver honours it.
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", IsNarrator = false };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p =>
                p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, b.ItemId("item"), character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("item")))!.CharacterId);
        }

        [Fact]
        public async Task SetItemCharacterCommand_AssignsNarratorToDialogItem()
        {
            // The reverse gesture: a narrative aside the splitter mistook for dialog becomes
            // narration by stamping the narrator sentinel.
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", IsNarrator = false };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p =>
                p.AddCharacterLine("item", "Hello world", speaker: "alice")))).BuildAsync();

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, b.ItemId("item"), ProjectDbContext.NarratorId));

            await using var verify = await OpenDbAsync();
            Assert.Equal(ProjectDbContext.NarratorId, (await verify.ParagraphItems.FindAsync(b.ItemId("item")))!.CharacterId);
        }

        [Fact]
        public async Task SetItemCharacterCommand_ClearsSpeakerOnNarrationItem()
        {
            // Clearing is the repair path for a narration item that already carries a character,
            // so it must stay open whatever the item's type.
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", IsNarrator = false };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p =>
                p.AddRawItem("item", ParagraphItemType.Narration, "Hello world", character.Id)))).BuildAsync();

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, b.ItemId("item"), null));

            await using var verify = await OpenDbAsync();
            Assert.Null((await verify.ParagraphItems.FindAsync(b.ItemId("item")))!.CharacterId);
        }

        // ---------------------------------------------------------------
        // A manual flip clears the item's generated audio (ADR-0006)
        // ---------------------------------------------------------------

        private async Task SeedAudioAsync(params Guid[] itemIds)
        {
            await using var db = await OpenDbAsync();
            foreach (var id in itemIds)
                (await db.ParagraphItems.FindAsync(id))!.AudioFileName = $"audio/{id}.wav";
            await db.SaveChangesAsync();
        }

        private async Task<string?> AudioFileNameOfAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return (await db.ParagraphItems.FindAsync(itemId))!.AudioFileName;
        }

        [Fact]
        public async Task SetItemCharacterCommand_ChangingSpeaker_DropsGeneratedAudio()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p =>
                p.AddNarration("item", "Hello world")))).BuildAsync();
            await SeedAudioAsync(b.ItemId("item"));

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, b.ItemId("item"), character.Id));

            Assert.Null(await AudioFileNameOfAsync(b.ItemId("item")));
        }

        [Fact]
        public async Task SetItemCharacterCommand_ClearingSpeaker_DropsGeneratedAudio()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p =>
                p.AddCharacterLine("item", "Hello world", speaker: "alice")))).BuildAsync();
            await SeedAudioAsync(b.ItemId("item"));

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, b.ItemId("item"), null));

            Assert.Null(await AudioFileNameOfAsync(b.ItemId("item")));
        }

        [Fact]
        public async Task SetItemCharacterCommand_SameSpeaker_KeepsGeneratedAudio()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p =>
                p.AddCharacterLine("item", "Hello world", speaker: "alice")))).BuildAsync();
            await SeedAudioAsync(b.ItemId("item"));

            await _svc.ExecuteAsync(new SetItemCharacterCommand(_folder, b.ItemId("item"), character.Id));

            Assert.NotNull(await AudioFileNameOfAsync(b.ItemId("item")));
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_DropsAudioOnlyFromItemsItMoves()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var bob = new Character { Id = Guid.NewGuid(), Name = "Bob" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            b.WithCharacter("bob", bob);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddNarration("narration", "N")
                    .AddCharacterLine("moved", "A", speaker: "alice")
                    .AddCharacterLine("alreadyBob", "B", speaker: "bob"))))
                .BuildAsync();
            await SeedAudioAsync(b.ItemId("narration"), b.ItemId("moved"), b.ItemId("alreadyBob"));

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), bob.Id));

            Assert.Null(await AudioFileNameOfAsync(b.ItemId("moved")));
            Assert.NotNull(await AudioFileNameOfAsync(b.ItemId("narration")));    // never swept
            Assert.NotNull(await AudioFileNameOfAsync(b.ItemId("alreadyBob")));   // swept, unmoved
        }

        [Fact]
        public async Task AttributeItemsCommand_LeavesGeneratedAudioAlone()
        {
            // The deliberate asymmetry: an LLM stamp must not invalidate audio across a whole
            // book on the next queue run. Recorded as a known gap in ADR-0006.
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddRawItem("item", ParagraphItemType.Character, "\"Hello.\""))))
                .BuildAsync();
            await SeedAudioAsync(b.ItemId("item"));

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("para"),
                [new ItemAttribution(b.ItemId("item"), alice.Id, null)]));

            await using var verify = await OpenDbAsync();
            var item = await verify.ParagraphItems.FindAsync(b.ItemId("item"));
            Assert.Equal(alice.Id, item!.CharacterId);
            Assert.NotNull(item.AudioFileName);
        }

        // ---------------------------------------------------------------
        // ClearBookContentCommand
        // ---------------------------------------------------------------

        [Fact]
        public async Task ClearBookContentCommand_RemovesAllHierarchy()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c.AddParagraph(configure: p => p.AddNarration("item", "Hello world")))).BuildAsync();

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

        [Fact]
        public async Task AddPausesCommand_InsertsPauseParagraphs()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddChapter("ch1", c => c
                    .AddParagraph(configure: p => p.AddNarration("itemA", "A"))
                    .AddParagraph(configure: p => p.AddNarration("itemB", "B")))
                .AddChapter("ch2", c => c
                    .AddParagraph(configure: p => p.AddNarration("itemC", "C"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new AddPausesCommand(_folder));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.ParagraphItems.AnyAsync(i => i.ItemType == ParagraphItemType.ParagraphPause));
            Assert.True(await verify.ParagraphItems.AnyAsync(i => i.ItemType == ParagraphItemType.ChapterPause));
        }

        [Fact]
        public async Task AddPausesCommand_IsIdempotent()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddChapter("ch1", c => c
                    .AddParagraph(configure: p => p.AddNarration("itemA", "A"))
                    .AddParagraph(configure: p => p.AddNarration("itemB", "B")))
                .AddChapter("ch2", c => c
                    .AddParagraph(configure: p => p.AddNarration("itemC", "C"))))
                .BuildAsync();

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
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph("para", p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new InsertPauseParagraphCommand(_folder, b.ItemId("item"), PauseInsertPosition.Before, kind));

            await using var verify = await OpenDbAsync();
            var paragraphs = await verify.Paragraphs
                .Where(p => p.ChapterId == b.ChapterId("ch"))
                .OrderBy(p => p.Order)
                .ToListAsync();
            Assert.Equal(2, paragraphs.Count);
            var pausePara = paragraphs[0];
            Assert.NotEqual(b.ParagraphId("para"), pausePara.Id);
            var pauseItem = await verify.ParagraphItems.SingleAsync(i => i.ParagraphId == pausePara.Id);
            Assert.Equal(expectedType, pauseItem.ItemType);
            Assert.Equal(b.ParagraphId("para"), paragraphs[1].Id);
        }

        [Fact]
        public async Task InsertPauseParagraphCommand_After_InsertsPauseAfterParagraph()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph("para", p => p.AddNarration("item", "Hello world")))).BuildAsync();

            await _svc.ExecuteAsync(new InsertPauseParagraphCommand(_folder, b.ItemId("item"), PauseInsertPosition.After, PauseKind.ParagraphPause));

            await using var verify = await OpenDbAsync();
            var paragraphs = await verify.Paragraphs
                .Where(p => p.ChapterId == b.ChapterId("ch"))
                .OrderBy(p => p.Order)
                .ToListAsync();
            Assert.Equal(2, paragraphs.Count);
            Assert.Equal(b.ParagraphId("para"), paragraphs[0].Id);
            var pauseItem = await verify.ParagraphItems.SingleAsync(i => i.ParagraphId == paragraphs[1].Id);
            Assert.Equal(ParagraphItemType.ParagraphPause, pauseItem.ItemType);
        }

        [Fact]
        public async Task InsertPauseParagraphCommand_Between_InsertsPauseBetweenExistingParagraphs()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddChapter("ch", c => c
                    .AddParagraph("para", p => p.AddNarration("item", "Hello world"))
                    .AddParagraph("para2", p => p.AddNarration("item2", "Second"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new InsertPauseParagraphCommand(_folder, b.ItemId("item"), PauseInsertPosition.After, PauseKind.ChapterPause));

            await using var verify = await OpenDbAsync();
            var paragraphs = await verify.Paragraphs
                .Where(p => p.ChapterId == b.ChapterId("ch"))
                .OrderBy(p => p.Order)
                .ToListAsync();
            Assert.Equal(3, paragraphs.Count);
            Assert.Equal(b.ParagraphId("para"),  paragraphs[0].Id);
            Assert.Equal(b.ParagraphId("para2"), paragraphs[2].Id);
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
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b
                .AddVolume("vol1", v => v.AddPart("part"))
                .AddVolume("vol2")
                .BuildAsync();

            await using var db = await OpenDbAsync();
            var trackedPart = await db.Parts.FindAsync(b.PartId("part"));
            db.Entry(trackedPart!).State = EntityState.Detached;

            trackedPart!.VolumeId = b.VolumeId("vol2");

            var mutation = new HierarchyMutation(ToAdd: [], ToDelete: [], ToUpdate: [trackedPart]);
            await BookCommandHandler.ApplyMutationAsync(db, mutation);
            await db.DisposeAsync();

            await using var verify = await OpenDbAsync();
            var saved = await verify.Parts.FindAsync(b.PartId("part"));
            Assert.Equal(b.VolumeId("vol2"), saved!.VolumeId);
        }

        // ---------------------------------------------------------------
        // CreateCharacterCommand
        // ---------------------------------------------------------------

        [Fact]
        public async Task CreateCharacterCommand_CreatesCharacter()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

            var result = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Mr. Hyde"));

            Assert.NotNull(result);
            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Name == "Mr. Hyde" && c.Id == result.Value));
        }

        [Fact]
        public async Task CreateCharacterCommand_ReusesExistingByName()
        {
            var existing = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", existing);
            await b.BuildAsync();

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
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddRawItem("charItem1", ParagraphItemType.Character, "Hello")
                    .AddRawItem("charItem2", ParagraphItemType.Character, "World")
                    .AddNarration("narrationItem", "Narration"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("charItem1")))!.CharacterId);
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("charItem2")))!.CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await verify.ParagraphItems.FindAsync(b.ItemId("narrationItem")))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_WithVoiceInstructions_PersistsOnCharacterItems()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddRawItem("charItem", ParagraphItemType.Character, "Hello"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), character.Id, "whispering, tense"));

            await using var verify = await OpenDbAsync();
            var item = await verify.ParagraphItems.FindAsync(b.ItemId("charItem"));
            Assert.Equal(character.Id, item!.CharacterId);
            Assert.Equal("whispering, tense", item.VoiceInstructions);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_NullVoiceInstructions_DoesNotClobberExisting()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Bob" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("bob", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddRawItem("charItem", ParagraphItemType.Character, "Hello"))))
                .BuildAsync();

            // Seed voice instructions directly after build
            await using var seed = await OpenDbAsync();
            var charItem = await seed.ParagraphItems.FindAsync(b.ItemId("charItem"));
            charItem!.VoiceInstructions = "existing voice";
            await seed.SaveChangesAsync();
            await seed.DisposeAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), character.Id));

            await using var verify = await OpenDbAsync();
            var item = await verify.ParagraphItems.FindAsync(b.ItemId("charItem"));
            Assert.Equal(character.Id, item!.CharacterId);
            Assert.Equal("existing voice", item.VoiceInstructions);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_WithId_SetsAllCharacterItemsLeavesNarrationUntouched()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddRawItem("ci1", ParagraphItemType.Character, "A")
                    .AddRawItem("ci2", ParagraphItemType.Character, "B")
                    .AddNarration("ni", "N"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("ci1")))!.CharacterId);
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("ci2")))!.CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await verify.ParagraphItems.FindAsync(b.ItemId("ni")))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_MixedParagraph_SweepsOnlyNonNarratorItems()
        {
            // Narration + attributed dialog + unattributed dialog. The sweep is the old
            // "dialog only" rule expressed against the speaker (ADR-0006).
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var bob = new Character { Id = Guid.NewGuid(), Name = "Bob" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            b.WithCharacter("bob", bob);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddNarration("narration", "N")
                    .AddCharacterLine("attributed", "A", speaker: "alice")
                    .AddRawItem("unattributed", ParagraphItemType.Character, "U"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), bob.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(ProjectDbContext.NarratorId, (await verify.ParagraphItems.FindAsync(b.ItemId("narration")))!.CharacterId);
            Assert.Equal(bob.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("attributed")))!.CharacterId);
            Assert.Equal(bob.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("unattributed")))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_ToNarrator_MakesParagraphNarrationAndIsIdempotent()
        {
            // The one-gesture repair for a paragraph the LLM swallowed into a character.
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddNarration("narration", "N")
                    .AddCharacterLine("attributed", "A", speaker: "alice")
                    .AddRawItem("unattributed", ParagraphItemType.Character, "U"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), ProjectDbContext.NarratorId));
            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), ProjectDbContext.NarratorId));

            await using var verify = await OpenDbAsync();
            foreach (var name in new[] { "narration", "attributed", "unattributed" })
                Assert.Equal(ProjectDbContext.NarratorId, (await verify.ParagraphItems.FindAsync(b.ItemId(name)))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_WithNullId_ClearsAllCharacterItems()
        {
            var existingChar = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", existingChar);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p
                    .AddRawItem("ci1", ParagraphItemType.Character, "A", existingChar.Id)
                    .AddRawItem("ci2", ParagraphItemType.Character, "B", existingChar.Id))))
                .BuildAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para"), null));

            await using var verify = await OpenDbAsync();
            Assert.Null((await verify.ParagraphItems.FindAsync(b.ItemId("ci1")))!.CharacterId);
            Assert.Null((await verify.ParagraphItems.FindAsync(b.ItemId("ci2")))!.CharacterId);
        }

        [Fact]
        public async Task SetParagraphCharacterCommand_DoesNotTouchOtherParagraphs()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para1", p => p
                    .AddRawItem("target", ParagraphItemType.Character, "T"))
                .AddParagraph("para2", p => p
                    .AddRawItem("other", ParagraphItemType.Character, "O"))))
                .BuildAsync();

            await _svc.ExecuteAsync(new SetParagraphCharacterCommand(_folder, b.ParagraphId("para1"), character.Id));

            await using var verify = await OpenDbAsync();
            Assert.Equal(character.Id, (await verify.ParagraphItems.FindAsync(b.ItemId("target")))!.CharacterId);
            Assert.Null((await verify.ParagraphItems.FindAsync(b.ItemId("other")))!.CharacterId);
        }
    }
}
