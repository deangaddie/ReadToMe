using System;
using System.Threading;
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
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class DeleteCharacterHandlerTests : ProjectDbTestBase
    {
        private readonly DeleteCharacterHandler _handler;
        private readonly ProjectFolderId _folder;

        public DeleteCharacterHandlerTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _handler = new DeleteCharacterHandler(session);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<(Guid charId, Guid paraId, Guid itemId)> SeedCharacterWithParagraphAsync()
        {
            await using var db = await OpenDbAsync();
            db.Projects.Add(new Project { Title = "T", BookTitle = "B", Author = "A", Filename = "t.epub", Type = BookFileType.Epub });
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Characters.Add(character);

            var vol  = new Volume    { Id = Guid.NewGuid(), Title = "V", Order = Key() };
            var part = new Part      { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch   = new Chapter   { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(), CharacterId = character.Id };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character,
                Order = Key(), CharacterId = character.Id, Text = "\"Hello.\""
            };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch);
            db.Paragraphs.Add(para); db.ParagraphItems.Add(item);

            db.CharacterAliases.Add(new CharacterAlias { Id = Guid.NewGuid(), CharacterId = character.Id, Name = "Al" });
            await db.SaveChangesAsync();
            return (character.Id, para.Id, item.Id);
        }

        [Fact]
        public async Task DeleteCharacter_Narrator_IsNoOp()
        {
            await using var db = await OpenDbAsync();
            db.Projects.Add(new Project { Title = "T", BookTitle = "B", Author = "A", Filename = "t.epub", Type = BookFileType.Epub });
            await db.SaveChangesAsync();

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, ProjectDbContext.NarratorId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            // Narrator should still exist (seeded by migration)
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
        }

        [Fact]
        public async Task DeleteCharacter_NullsParagraphItemAndParagraphCharacterIds_AndDeletesAliases()
        {
            var (charId, paraId, itemId) = await SeedCharacterWithParagraphAsync();

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, charId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == charId));
            Assert.False(await verify.CharacterAliases.AnyAsync(a => a.CharacterId == charId));

            var para = await verify.Paragraphs.FindAsync(paraId);
            Assert.Null(para!.CharacterId);

            var item = await verify.ParagraphItems.FindAsync(itemId);
            Assert.Null(item!.CharacterId);
        }

        [Fact]
        public async Task DeleteCharacter_NotInDb_DoesNotThrow()
        {
            await using var db = await OpenDbAsync();
            db.Projects.Add(new Project { Title = "T", BookTitle = "B", Author = "A", Filename = "t.epub", Type = BookFileType.Epub });
            await db.SaveChangesAsync();

            var missingId = Guid.NewGuid();
            var ex = await Record.ExceptionAsync(() =>
                _handler.HandleAsync(new DeleteCharacterCommand(_folder, missingId), CancellationToken.None));

            Assert.Null(ex);
        }
    }
}
