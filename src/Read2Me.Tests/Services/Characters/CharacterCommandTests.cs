using System;
using System.Linq;
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
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class CharacterCommandTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public CharacterCommandTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _svc = new BookCommandHandler(session, fs);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<ProjectDbContext> SeedProjectAsync()
        {
            var db = await OpenDbAsync();
            db.Projects.Add(new Project
            {
                Title = "T", BookTitle = "B", Author = "A",
                Filename = "t.epub", Type = BookFileType.Epub
            });
            await db.SaveChangesAsync();
            return db;
        }

        private async Task<(Chapter ch, Paragraph para, ParagraphItem item)> SeedCharacterParagraphAsync(
            ProjectDbContext db, Guid characterId)
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(), CharacterId = characterId };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character,
                Order = Key(), CharacterId = characterId, Text = "\"Hello.\""
            };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch);
            db.Paragraphs.Add(para); db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();
            return (ch, para, item);
        }

        // ---------------------------------------------------------------
        // AddCharacterAlias
        // ---------------------------------------------------------------

        [Fact]
        public async Task AddAlias_InsertsRow()
        {
            await using var db = await SeedProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Al"));

            await using var verify = await OpenDbAsync();
            var alias = await verify.CharacterAliases.SingleAsync(a => a.CharacterId == charId.Value);
            Assert.Equal("Al", alias.Name);
        }

        [Fact]
        public async Task AddAlias_Idempotent_WhenDuplicateName()
        {
            await using var db = await SeedProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Al"));
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "al")); // case-insensitive duplicate

            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.CharacterAliases.CountAsync(a => a.CharacterId == charId.Value));
        }

        [Fact]
        public async Task AddAlias_Idempotent_WhenSameAsCanonicalName()
        {
            await using var db = await SeedProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "alice"));

            await using var verify = await OpenDbAsync();
            Assert.Equal(0, await verify.CharacterAliases.CountAsync(a => a.CharacterId == charId.Value));
        }

        [Fact]
        public async Task RemoveAlias_DeletesRow()
        {
            await using var db = await SeedProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Bob"));
            await db.DisposeAsync();
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Bobby"));

            await using var db2 = await OpenDbAsync();
            var aliasId = (await db2.CharacterAliases.SingleAsync(a => a.CharacterId == charId.Value)).Id;
            await db2.DisposeAsync();

            await _svc.ExecuteAsync(new RemoveCharacterAliasCommand(_folder, aliasId));

            await using var verify = await OpenDbAsync();
            Assert.Equal(0, await verify.CharacterAliases.CountAsync(a => a.CharacterId == charId.Value));
        }

        // ---------------------------------------------------------------
        // MergeCharacters
        // ---------------------------------------------------------------

        [Fact]
        public async Task Merge_ReassignsParagraphItemsToSurvivor()
        {
            await using var db = await SeedProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            var (_, _, item) = await SeedCharacterParagraphAsync(db, mergedId!.Value);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId!.Value, false));

            await using var verify = await OpenDbAsync();
            var reloadedItem = await verify.ParagraphItems.FindAsync(item.Id);
            Assert.Equal(survivorId.Value, reloadedItem!.CharacterId);
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == mergedId.Value));
        }

        [Fact]
        public async Task Merge_AddsMergedNameAsAlias_WhenFlagSet()
        {
            await using var db = await SeedProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId!.Value, true));

            await using var verify = await OpenDbAsync();
            var alias = await verify.CharacterAliases.SingleOrDefaultAsync(
                a => a.CharacterId == survivorId.Value && a.Name == "Merged");
            Assert.NotNull(alias);
        }

        [Fact]
        public async Task Merge_DoesNotAddAlias_WhenFlagFalse()
        {
            await using var db = await SeedProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId!.Value, false));

            await using var verify = await OpenDbAsync();
            Assert.Equal(0, await verify.CharacterAliases.CountAsync(a => a.CharacterId == survivorId.Value));
        }

        [Fact]
        public async Task Merge_MovesAliasesFromMergedToSurvivor()
        {
            await using var db = await SeedProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            await db.DisposeAsync();
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, mergedId!.Value, "MergedAlias"));

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId!.Value, false));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.CharacterAliases.AnyAsync(
                a => a.CharacterId == survivorId.Value && a.Name == "MergedAlias"));
        }

        [Fact]
        public async Task Merge_IgnoresNarrator_WhenNarratorIsMerged()
        {
            await using var db = await SeedProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            await db.DisposeAsync();

            // Should be a no-op (narrator guarded)
            await _svc.ExecuteAsync(new MergeCharactersCommand(
                _folder, survivorId!.Value, ProjectDbContext.NarratorId, false));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
        }

        // ---------------------------------------------------------------
        // DeleteCharacter
        // ---------------------------------------------------------------

        [Fact]
        public async Task Delete_UnlinksParagraphItems_DoesNotDeleteThem()
        {
            await using var db = await SeedProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));
            var (_, _, item) = await SeedCharacterParagraphAsync(db, charId!.Value);
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new DeleteCharacterCommand(_folder, charId.Value));

            await using var verify = await OpenDbAsync();
            // Character deleted
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == charId.Value));
            // Item still exists but unlinked
            var reloadedItem = await verify.ParagraphItems.FindAsync(item.Id);
            Assert.NotNull(reloadedItem);
            Assert.Null(reloadedItem!.CharacterId);
        }

        [Fact]
        public async Task Delete_RemovesAliases()
        {
            await using var db = await SeedProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));
            await db.DisposeAsync();
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Al"));

            await _svc.ExecuteAsync(new DeleteCharacterCommand(_folder, charId.Value));

            await using var verify = await OpenDbAsync();
            Assert.Equal(0, await verify.CharacterAliases.CountAsync(a => a.CharacterId == charId.Value));
        }

        [Fact]
        public async Task Delete_IgnoresNarrator()
        {
            await using var db = await SeedProjectAsync();
            await db.DisposeAsync();

            await _svc.ExecuteAsync(new DeleteCharacterCommand(_folder, ProjectDbContext.NarratorId));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
        }
    }
}
