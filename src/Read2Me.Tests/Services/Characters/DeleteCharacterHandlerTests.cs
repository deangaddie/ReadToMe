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
        private readonly ProjectDbSession _session;
        private readonly ProjectFolderId _folder;

        public DeleteCharacterHandlerTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            _session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _handler = new DeleteCharacterHandler(_session);
            _folder = new ProjectFolderId(FolderName);
        }

        private async Task<(Guid charId, Guid paraId, Guid itemId)> SeedCharacterWithParagraphAsync(
            Guid? characterId = null,
            Guid? narratorCharacterId = null)
        {
            var character = new Character { Id = characterId ?? Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            if (narratorCharacterId.HasValue) b.WithNarratorLink(narratorCharacterId.Value);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p.AddRawItem("item", ParagraphItemType.Speech, "\"Hello.\"", character.Id))))
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
        public async Task DeleteCharacter_WithVoiceAndRules_DeletesVoicesAndRules()
        {
            var (charId, _, _) = await SeedCharacterWithParagraphAsync();

            var voiceId = Guid.NewGuid();
            await using (var seed = await OpenDbAsync())
            {
                seed.Voices.Add(new Read2Me.Data.Entities.Voice
                {
                    Id = voiceId,
                    CharacterId = charId,
                    Name = "Alice Voice",
                    Source = VoiceSource.Generated
                });
                seed.VoiceRules.Add(new VoiceRule
                {
                    Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceId, Rank = "a0", IsDefault = true
                });
                seed.VoiceRules.Add(new VoiceRule
                {
                    Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceId, Rank = "b0", IsDefault = false,
                    FromLevel = VoiceAnchorLevel.Chapter, FromNodeId = Guid.NewGuid()
                });
                await seed.SaveChangesAsync();
            }

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, charId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == charId));
            Assert.False(await verify.Voices.AnyAsync(v => v.CharacterId == charId));
            Assert.False(await verify.VoiceRules.AnyAsync(r => r.CharacterId == charId));
        }

        [Fact]
        public async Task DeleteCharacter_VoiceReferencedByAnotherCharactersRule_DeletesThatRuleToo()
        {
            var (charId, _, _) = await SeedCharacterWithParagraphAsync();

            var voiceId = Guid.NewGuid();
            var otherCharId = Guid.NewGuid();
            await using (var seed = await OpenDbAsync())
            {
                seed.Characters.Add(new Character { Id = otherCharId, Name = "Bob" });
                seed.Voices.Add(new Read2Me.Data.Entities.Voice
                {
                    Id = voiceId,
                    CharacterId = charId,
                    Name = "Alice Voice",
                    Source = VoiceSource.Generated
                });
                // Bob borrows Alice's voice — Restrict FK on VoiceRules.VoiceId.
                seed.VoiceRules.Add(new VoiceRule
                {
                    Id = Guid.NewGuid(), CharacterId = otherCharId, VoiceId = voiceId, Rank = "a0", IsDefault = true
                });
                await seed.SaveChangesAsync();
            }

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, charId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Voices.AnyAsync(v => v.Id == voiceId));
            Assert.False(await verify.VoiceRules.AnyAsync(r => r.VoiceId == voiceId));
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == otherCharId));
        }

        /// <summary>
        /// Deleting the character a book narrates with has to clear the link in the same
        /// transaction — otherwise the column is left pointing at a row that no longer exists and
        /// only <see cref="NarratorIdentity"/>'s dangling-link fallback keeps audio alive.
        /// </summary>
        [Fact]
        public async Task DeleteCharacter_Linked_ClearsTheNarratorLink()
        {
            var charId = Guid.NewGuid();
            await SeedCharacterWithParagraphAsync(charId, narratorCharacterId: charId);

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, charId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            // The column, not just the projection: LoadAsync would report Unlinked either way,
            // because a link to a deleted row falls back. This asserts the fallback is not what
            // saved us. (Sanctioned raw read — see NarratorCharacterIdAccessRuleTests.)
            Assert.Null(await verify.Projects.Select(p => p.NarratorCharacterId).FirstAsync());
            Assert.Equal(NarratorIdentity.Unlinked, await NarratorIdentity.LoadAsync(verify));
        }

        [Fact]
        public async Task DeleteCharacter_NotLinked_LeavesTheNarratorLinkAlone()
        {
            var linkedId = Guid.NewGuid();
            var (charId, _, _) = await SeedCharacterWithParagraphAsync();
            await using (var seed = await OpenDbAsync())
            {
                seed.Characters.Add(new Character { Id = linkedId, Name = "Watson" });
                await seed.SaveChangesAsync();
            }
            await new SetNarratorCharacterHandler(_session)
                .HandleAsync(new SetNarratorCharacterCommand(_folder, linkedId), CancellationToken.None);

            await _handler.HandleAsync(new DeleteCharacterCommand(_folder, charId), CancellationToken.None);

            await using var verify = await OpenDbAsync();
            var narrator = await NarratorIdentity.LoadAsync(verify);
            Assert.True(narrator.IsLinked);
            Assert.Equal(linkedId, narrator.CharacterId);
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
