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

        private async Task<(Guid charId, Guid paraId, Guid itemId)> SeedCharacterWithParagraphAsync()
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p.AddRawItem("item", ParagraphItemType.Character, "\"Hello.\"", character.Id))))
                .BuildAsync();

            // CharacterAlias needs post-build seeding
            await using var db = await OpenDbAsync();
            db.CharacterAliases.Add(new CharacterAlias { Id = Guid.NewGuid(), CharacterId = character.Id, Name = "Al" });
            await db.SaveChangesAsync();

            return (character.Id, b.ParagraphId("para"), b.ItemId("item"));
        }

        [Fact]
        public async Task DeleteCharacter_Narrator_IsNoOp()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, ProjectDbContext.NarratorId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
        }

        [Fact]
        public async Task DeleteCharacter_NullsParagraphItemCharacterIds_AndDeletesAliases()
        {
            var (charId, _, itemId) = await SeedCharacterWithParagraphAsync();

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, charId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == charId));
            Assert.False(await verify.CharacterAliases.AnyAsync(a => a.CharacterId == charId));

            var item = await verify.ParagraphItems.FindAsync(itemId);
            Assert.Null(item!.CharacterId);
        }

        [Fact]
        public async Task DeleteCharacter_NotInDb_DoesNotThrow()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

            var missingId = Guid.NewGuid();
            var ex = await Record.ExceptionAsync(() =>
                _handler.HandleAsync(new DeleteCharacterCommand(_folder, missingId), CancellationToken.None));

            Assert.Null(ex);
        }
    }
}
