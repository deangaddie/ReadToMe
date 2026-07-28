using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
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
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            _svc = sp.GetRequiredService<BookCommandHandler>();
            _folder = new ProjectFolderId(FolderName);
        }

        private async Task InitProjectAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();
        }

        private async Task<Guid> SeedCharacterParagraphWithCharIdAsync(Guid characterId)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p.AddRawItem("item", ParagraphItemType.Character, "\"Hello.\"", characterId))))
                .AddHierarchyAsync();

            return b.ItemId("item");
        }

        // ---------------------------------------------------------------
        // AddCharacterAlias
        // ---------------------------------------------------------------

        [Fact]
        public async Task AddAlias_InsertsRow()
        {
            await InitProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));

            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Al"));

            await using var verify = await OpenDbAsync();
            var alias = await verify.CharacterAliases.SingleAsync(a => a.CharacterId == charId.Value);
            Assert.Equal("Al", alias.Name);
        }

        [Fact]
        public async Task AddAlias_Idempotent_WhenDuplicateName()
        {
            await InitProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));

            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Al"));
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "al")); // case-insensitive duplicate

            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.CharacterAliases.CountAsync(a => a.CharacterId == charId.Value));
        }

        [Fact]
        public async Task AddAlias_Idempotent_WhenSameAsCanonicalName()
        {
            await InitProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));

            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "alice"));

            await using var verify = await OpenDbAsync();
            Assert.Equal(0, await verify.CharacterAliases.CountAsync(a => a.CharacterId == charId.Value));
        }

        [Fact]
        public async Task RemoveAlias_DeletesRow()
        {
            await InitProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Bob"));
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Bobby"));

            Guid aliasId;
            await using (var db2 = await OpenDbAsync())
                aliasId = (await db2.CharacterAliases.SingleAsync(a => a.CharacterId == charId.Value)).Id;

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
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            var itemId = await SeedCharacterParagraphWithCharIdAsync(mergedId!.Value);

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId.Value, false));

            await using var verify = await OpenDbAsync();
            var reloadedItem = await verify.ParagraphItems.FindAsync(itemId);
            Assert.Equal(survivorId.Value, reloadedItem!.CharacterId);
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == mergedId.Value));
        }

        [Fact]
        public async Task Merge_AddsMergedNameAsAlias_WhenFlagSet()
        {
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId!.Value, true));

            await using var verify = await OpenDbAsync();
            var alias = await verify.CharacterAliases.SingleOrDefaultAsync(
                a => a.CharacterId == survivorId.Value && a.Name == "Merged");
            Assert.NotNull(alias);
        }

        [Fact]
        public async Task Merge_DoesNotAddAlias_WhenFlagFalse()
        {
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId!.Value, false));

            await using var verify = await OpenDbAsync();
            Assert.Equal(0, await verify.CharacterAliases.CountAsync(a => a.CharacterId == survivorId.Value));
        }

        [Fact]
        public async Task Merge_MovesAliasesFromMergedToSurvivor()
        {
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, mergedId!.Value, "MergedAlias"));

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId.Value, false));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.CharacterAliases.AnyAsync(
                a => a.CharacterId == survivorId.Value && a.Name == "MergedAlias"));
        }

        [Fact]
        public async Task Merge_DeletesMergedVoicesAndRules_WhenMergedHasAVoice()
        {
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            var voiceId = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, mergedId!.Value, "MergedVoice"));

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId.Value, true));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == mergedId.Value));
            Assert.Null(await verify.Voices.FindAsync(voiceId!.Value));
            Assert.Empty(await verify.VoiceRules.Where(r => r.VoiceId == voiceId.Value).ToListAsync());
        }

        [Fact]
        public async Task Merge_LeavesSurvivorVoicesAndDefaultRuleAlone()
        {
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            var survivorVoiceId = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, survivorId!.Value, "SurvivorVoice"));
            await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, mergedId!.Value, "MergedVoice"));

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId.Value, mergedId.Value, true));

            await using var verify = await OpenDbAsync();
            var voice = Assert.Single(await verify.Voices.Where(v => v.CharacterId == survivorId.Value).ToListAsync());
            Assert.Equal(survivorVoiceId!.Value, voice.Id);
            var rule = Assert.Single(await verify.VoiceRules.Where(r => r.CharacterId == survivorId.Value).ToListAsync());
            Assert.True(rule.IsDefault);
            Assert.Equal(survivorVoiceId.Value, rule.VoiceId);
        }

        /// <summary>
        /// A positional rule owned by another character can point at the merged character's voice.
        /// VoiceRules.VoiceId is Restrict, so that rule has to go too or the merge dies on the FK.
        /// </summary>
        [Fact]
        public async Task Merge_DeletesForeignRulesPointingAtMergedVoices()
        {
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));
            var mergedId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Merged"));
            var otherId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Other"));
            var voiceId = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, mergedId!.Value, "MergedVoice"));
            await _svc.ExecuteAsync(new CreateVoiceRuleCommand(
                _folder, otherId!.Value, voiceId!.Value, null, null, null, null));

            await _svc.ExecuteAsync(new MergeCharactersCommand(_folder, survivorId!.Value, mergedId.Value, true));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == mergedId.Value));
            Assert.Empty(await verify.VoiceRules.Where(r => r.VoiceId == voiceId.Value).ToListAsync());
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == otherId.Value));
        }

        [Fact]
        public async Task Merge_IgnoresNarrator_WhenNarratorIsMerged()
        {
            await InitProjectAsync();
            var survivorId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Survivor"));

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
            await InitProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));
            var itemId = await SeedCharacterParagraphWithCharIdAsync(charId!.Value);

            await _svc.ExecuteAsync(new DeleteCharacterCommand(_folder, charId.Value));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == charId.Value));
            var reloadedItem = await verify.ParagraphItems.FindAsync(itemId);
            Assert.NotNull(reloadedItem);
            Assert.Null(reloadedItem!.CharacterId);
        }

        [Fact]
        public async Task Delete_RemovesAliases()
        {
            await InitProjectAsync();
            var charId = await _svc.ExecuteAsync(new CreateCharacterCommand(_folder, "Alice"));
            await _svc.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Al"));

            await _svc.ExecuteAsync(new DeleteCharacterCommand(_folder, charId.Value));

            await using var verify = await OpenDbAsync();
            Assert.Equal(0, await verify.CharacterAliases.CountAsync(a => a.CharacterId == charId.Value));
        }

        [Fact]
        public async Task Delete_IgnoresNarrator()
        {
            await InitProjectAsync();

            await _svc.ExecuteAsync(new DeleteCharacterCommand(_folder, ProjectDbContext.NarratorId));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
        }
    }
}
