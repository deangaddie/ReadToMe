using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Mutations
{
    /// <summary>
    /// The Character, narrator and policy family proved through
    /// <see cref="BookMutations.CommitAsync"/> against a real SQLite project (ADR 0007).
    /// <para>
    /// Three things matter beyond the lifecycle rules themselves. The receipt must report the facets
    /// this Book actually moved — a delete that took a Voice with it says so, one that took none does
    /// not — because a Book View reconciles from facts rather than from the mutation's name. A valid
    /// gesture that applies nothing must be <see cref="BookMutationOutcome.NoChange"/>, so that
    /// re-applying a discovery result or re-picking the current narrator costs no revision and no
    /// reconciliation anywhere. And the two refusals this family has — the protected seed Narrator row
    /// and a target the Book does not contain — must be explicit outcomes rather than a silent
    /// nothing, so a caller can tell "refused" from "done".
    /// </para>
    /// </summary>
    public class CharacterLifecycleMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public CharacterLifecycleMutationTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _folder = new ProjectFolderId(FolderName);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        /// <summary>Commits in its own scope, the way a producer does, and returns the outcome.</summary>
        private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
        }

        private async Task<BookMutationEffects> AppliedAsync(BookMutation mutation) =>
            Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(mutation)).Receipt.Effects;

        private async Task<BookMutationRejection> RefusedAsync(BookMutation mutation) =>
            Assert.IsType<BookMutationOutcome.Rejected>(await CommitAsync(mutation)).Reason;

        /// <summary>The Voice family is not migrated yet, so its seeding still goes through commands.</summary>
        private async Task<Guid?> CommandAsync(BookCommand command)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBookCommandHandler>().ExecuteAsync(command);
        }

        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();

        private BookHierarchyBuilder Builder() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .WithCharacter("bob", new Character { Id = BobId, Name = "Bob" });

        /// <summary>An empty Book with Alice and Bob on the roster — enough for every roster gesture.</summary>
        private Task SeedRosterAsync() => Builder().BuildAsync();

        /// <summary>Alice with one line to her name, so a merge or delete has attribution to move.</summary>
        private async Task<Guid> SeedAliceLineAsync()
        {
            var b = Builder();
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                    .AddParagraph("para", p => p.AddCharacterLine("item", "\"Hello.\"", "alice"))))
                .BuildAsync();
            return b.ItemId("item");
        }

        private async Task<Guid> AddAliasAsync(Guid characterId, string name)
        {
            await using var db = await OpenDbAsync();
            var alias = new CharacterAlias { Id = Guid.NewGuid(), CharacterId = characterId, Name = name };
            db.CharacterAliases.Add(alias);
            await db.SaveChangesAsync();
            return alias.Id;
        }

        // ── create ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Create_addsTheCharacter_andNamesItOnTheReceipt()
        {
            await SeedRosterAsync();

            var effects = await AppliedAsync(new CreateCharacterMutation(_folder, "Carol"));

            Assert.Equal(BookFacets.Characters, effects.Facets);
            Assert.Equal(BookMutationScope.Exact, effects.Scope);
            Assert.Empty(effects.ParagraphIds);

            await using var verify = await OpenDbAsync();
            var carol = await verify.Characters.SingleAsync(c => c.Name == "Carol");
            Assert.Equal(carol.Id, effects.CreatedId);
        }

        [Theory]
        [InlineData("Alice")]
        [InlineData("alice")]
        public async Task Create_changesNothing_whenTheNameIsAlreadyOnTheRoster(string name)
        {
            await SeedRosterAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(await CommitAsync(new CreateCharacterMutation(_folder, name)));

            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.Characters.CountAsync(c => c.Name == "Alice"));
        }

        [Fact]
        public async Task Create_changesNothing_whenAnAliasAlreadyAnswersToTheName()
        {
            await SeedRosterAsync();
            await AddAliasAsync(AliceId, "Ally");

            Assert.IsType<BookMutationOutcome.NoChange>(await CommitAsync(new CreateCharacterMutation(_folder, "ally")));

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Name == "ally"));
        }

        // ── rename ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Rename_changesTheName_andLeavesAliasesAndLinesAlone()
        {
            var itemId = await SeedAliceLineAsync();
            await AddAliasAsync(AliceId, "Ally");

            var effects = await AppliedAsync(new RenameCharacterMutation(_folder, AliceId, "Alicia"));

            Assert.Equal(BookFacets.Characters, effects.Facets);
            await using var verify = await OpenDbAsync();
            Assert.Equal("Alicia", (await verify.Characters.FindAsync(AliceId))!.Name);
            Assert.True(await verify.CharacterAliases.AnyAsync(a => a.CharacterId == AliceId && a.Name == "Ally"));
            Assert.Equal(AliceId, (await verify.ParagraphItems.FindAsync(itemId))!.CharacterId);
        }

        [Fact]
        public async Task Rename_changesNothing_whenTheNameIsTheOneItHas()
        {
            await SeedRosterAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new RenameCharacterMutation(_folder, AliceId, "Alice")));
        }

        /// <summary>
        /// A case-only rename is a real rename: the roster <em>matches</em> names case-insensitively,
        /// but what it displays is what was typed.
        /// </summary>
        [Fact]
        public async Task Rename_appliesACaseOnlyChange()
        {
            await SeedRosterAsync();

            await AppliedAsync(new RenameCharacterMutation(_folder, AliceId, "ALICE"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("ALICE", (await verify.Characters.FindAsync(AliceId))!.Name);
        }

        [Fact]
        public async Task Rename_refusesTheSeedNarratorRow()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.Validation,
                await RefusedAsync(new RenameCharacterMutation(_folder, ProjectDbContext.NarratorId, "Voice of God")));

            await using var verify = await OpenDbAsync();
            Assert.NotEqual("Voice of God", (await verify.Characters.FindAsync(ProjectDbContext.NarratorId))!.Name);
        }

        [Fact]
        public async Task Rename_refusesACharacterThisBookDoesNotHave()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new RenameCharacterMutation(_folder, Guid.NewGuid(), "Nobody")));
        }

        // ── aliases ──────────────────────────────────────────────────────────

        [Fact]
        public async Task AddAlias_insertsTheName()
        {
            await SeedRosterAsync();

            var effects = await AppliedAsync(new AddCharacterAliasMutation(_folder, AliceId, "Ally"));

            Assert.Equal(BookFacets.Characters, effects.Facets);
            await using var verify = await OpenDbAsync();
            Assert.True(await verify.CharacterAliases.AnyAsync(a => a.CharacterId == AliceId && a.Name == "Ally"));
        }

        [Theory]
        [InlineData("Ally")]   // the alias it already carries
        [InlineData("ally")]   // the same alias, differently cased
        [InlineData("Alice")]  // its own canonical name
        public async Task AddAlias_changesNothing_whenTheCharacterAlreadyAnswersToIt(string name)
        {
            await SeedRosterAsync();
            await AddAliasAsync(AliceId, "Ally");

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new AddCharacterAliasMutation(_folder, AliceId, name)));

            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.CharacterAliases.CountAsync(a => a.CharacterId == AliceId));
        }

        [Fact]
        public async Task AddAlias_refusesACharacterThisBookDoesNotHave()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new AddCharacterAliasMutation(_folder, Guid.NewGuid(), "Ally")));
        }

        [Fact]
        public async Task RemoveAlias_deletesTheRow()
        {
            await SeedRosterAsync();
            var aliasId = await AddAliasAsync(AliceId, "Ally");

            var effects = await AppliedAsync(new RemoveCharacterAliasMutation(_folder, aliasId));

            Assert.Equal(BookFacets.Characters, effects.Facets);
            await using var verify = await OpenDbAsync();
            Assert.False(await verify.CharacterAliases.AnyAsync(a => a.Id == aliasId));
        }

        [Fact]
        public async Task RemoveAlias_refusesAnAliasThisBookDoesNotHave()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new RemoveCharacterAliasMutation(_folder, Guid.NewGuid())));
        }

        // ── merge ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Merge_movesTheLinesToTheSurvivor_andReportsAttribution()
        {
            var itemId = await SeedAliceLineAsync();

            var effects = await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, false));

            Assert.Equal(BookMutationScope.WholeProject, effects.Scope);
            Assert.Equal(BookFacets.Characters | BookFacets.Attribution, effects.Facets);

            await using var verify = await OpenDbAsync();
            Assert.Equal(BobId, (await verify.ParagraphItems.FindAsync(itemId))!.CharacterId);
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == AliceId));
        }

        [Fact]
        public async Task Merge_reportsOnlyTheRosterFacet_whenTheMergedCharacterHadNothing()
        {
            await SeedRosterAsync();

            var effects = await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, false));

            Assert.Equal(BookFacets.Characters, effects.Facets);
        }

        [Fact]
        public async Task Merge_keepsTheMergedNameAndItsAliases_whenAsked()
        {
            await SeedRosterAsync();
            await AddAliasAsync(AliceId, "Ally");

            await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, AddNameAsAlias: true));

            await using var verify = await OpenDbAsync();
            var names = await verify.CharacterAliases.Where(a => a.CharacterId == BobId).Select(a => a.Name).ToListAsync();
            Assert.Contains("Alice", names);
            Assert.Contains("Ally", names);
        }

        [Fact]
        public async Task Merge_movesAliasesWithoutAddingTheName_whenNotAsked()
        {
            await SeedRosterAsync();
            await AddAliasAsync(AliceId, "Ally");

            await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, AddNameAsAlias: false));

            await using var verify = await OpenDbAsync();
            var names = await verify.CharacterAliases.Where(a => a.CharacterId == BobId).Select(a => a.Name).ToListAsync();
            Assert.Equal(["Ally"], names);
        }

        /// <summary>
        /// A name the survivor already answers to must not be added twice: one string resolving to
        /// two roster entries is exactly the ambiguity aliases exist to remove.
        /// </summary>
        [Fact]
        public async Task Merge_doesNotDuplicateANameTheSurvivorAlreadyAnswersTo()
        {
            await SeedRosterAsync();
            await AddAliasAsync(BobId, "Alice");

            await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, AddNameAsAlias: true));

            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.CharacterAliases.CountAsync(a => a.CharacterId == BobId && a.Name == "Alice"));
        }

        [Fact]
        public async Task Merge_takesTheMergedVoicesAndRules_andSaysSo()
        {
            await SeedRosterAsync();
            var voiceId = await CommandAsync(new CreateVoiceCommand(_folder, AliceId, "Alice Voice"));

            var effects = await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, false));

            Assert.Equal(BookFacets.Characters | BookFacets.Voices | BookFacets.VoiceRules, effects.Facets);
            await using var verify = await OpenDbAsync();
            Assert.Null(await verify.Voices.FindAsync(voiceId!.Value));
            Assert.Empty(await verify.VoiceRules.Where(r => r.VoiceId == voiceId.Value).ToListAsync());
        }

        [Fact]
        public async Task Merge_leavesTheSurvivorsOwnVoiceAndDefaultRuleAlone()
        {
            await SeedRosterAsync();
            var survivorVoiceId = await CommandAsync(new CreateVoiceCommand(_folder, BobId, "Bob Voice"));
            await CommandAsync(new CreateVoiceCommand(_folder, AliceId, "Alice Voice"));

            await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, false));

            await using var verify = await OpenDbAsync();
            var voice = Assert.Single(await verify.Voices.Where(v => v.CharacterId == BobId).ToListAsync());
            Assert.Equal(survivorVoiceId!.Value, voice.Id);
            var rule = Assert.Single(await verify.VoiceRules.Where(r => r.CharacterId == BobId).ToListAsync());
            Assert.True(rule.IsDefault);
            Assert.Equal(survivorVoiceId.Value, rule.VoiceId);
        }

        /// <summary>
        /// A rule owned by a third character can point at the merged character's Voice.
        /// <c>VoiceRules.VoiceId</c> is Restrict, so that rule has to go too or the merge dies on the FK.
        /// </summary>
        [Fact]
        public async Task Merge_takesAForeignRulePointingAtAMergedVoice()
        {
            await SeedRosterAsync();
            var otherId = (await AppliedAsync(new CreateCharacterMutation(_folder, "Carol"))).CreatedId!.Value;
            var voiceId = await CommandAsync(new CreateVoiceCommand(_folder, AliceId, "Alice Voice"));
            await CommandAsync(new CreateVoiceRuleCommand(_folder, otherId, voiceId!.Value, null, null, null, null));

            await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, false));

            await using var verify = await OpenDbAsync();
            Assert.Empty(await verify.VoiceRules.Where(r => r.VoiceId == voiceId.Value).ToListAsync());
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == otherId));
        }

        [Fact]
        public async Task Merge_movesTheNarratorLinkToTheSurvivor_andSaysSo()
        {
            await SeedRosterAsync();
            await AppliedAsync(new SetNarratorCharacterMutation(_folder, AliceId));

            var effects = await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, false));

            Assert.True(effects.Facets.HasFlag(BookFacets.Narrator));
            await using var verify = await OpenDbAsync();
            Assert.Equal(BobId, (await NarratorIdentity.LoadAsync(verify)).CharacterId);
        }

        [Fact]
        public async Task Merge_leavesTheNarratorLinkAlone_whenTheSurvivorIsTheLinkedCharacter()
        {
            await SeedRosterAsync();
            await AppliedAsync(new SetNarratorCharacterMutation(_folder, BobId));

            var effects = await AppliedAsync(new MergeCharactersMutation(_folder, BobId, AliceId, false));

            Assert.False(effects.Facets.HasFlag(BookFacets.Narrator));
            await using var verify = await OpenDbAsync();
            Assert.Equal(BobId, (await NarratorIdentity.LoadAsync(verify)).CharacterId);
        }

        [Theory]
        [InlineData(true)]   // the narrator as the one merged away
        [InlineData(false)]  // the narrator as the survivor
        public async Task Merge_refusesTheSeedNarratorRow(bool asMerged)
        {
            await SeedRosterAsync();

            var mutation = asMerged
                ? new MergeCharactersMutation(_folder, BobId, ProjectDbContext.NarratorId, false)
                : new MergeCharactersMutation(_folder, ProjectDbContext.NarratorId, BobId, false);

            Assert.Equal(BookMutationRejection.Validation, await RefusedAsync(mutation));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == BobId));
        }

        /// <summary>
        /// Applied, this would repoint Alice's lines at Alice and then delete her — a Character and
        /// her whole attribution destroyed in answer to a gesture that plainly meant nothing.
        /// </summary>
        [Fact]
        public async Task Merge_refusesToMergeACharacterIntoItself()
        {
            var itemId = await SeedAliceLineAsync();

            Assert.Equal(BookMutationRejection.Validation,
                await RefusedAsync(new MergeCharactersMutation(_folder, AliceId, AliceId, false)));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == AliceId));
            Assert.Equal(AliceId, (await verify.ParagraphItems.FindAsync(itemId))!.CharacterId);
        }

        [Fact]
        public async Task Merge_refusesACharacterThisBookDoesNotHave()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new MergeCharactersMutation(_folder, BobId, Guid.NewGuid(), false)));
            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new MergeCharactersMutation(_folder, Guid.NewGuid(), BobId, false)));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == BobId));
        }

        // ── delete ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Delete_keepsTheLinesAsUnattributedDialog_andTakesTheAliases()
        {
            var itemId = await SeedAliceLineAsync();
            await AddAliasAsync(AliceId, "Ally");

            var effects = await AppliedAsync(new DeleteCharacterMutation(_folder, AliceId));

            Assert.Equal(BookMutationScope.WholeProject, effects.Scope);
            Assert.Equal(BookFacets.Characters | BookFacets.Attribution, effects.Facets);

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Characters.AnyAsync(c => c.Id == AliceId));
            Assert.False(await verify.CharacterAliases.AnyAsync(a => a.CharacterId == AliceId));
            var item = await verify.ParagraphItems.FindAsync(itemId);
            Assert.NotNull(item);
            Assert.Null(item!.CharacterId);
        }

        [Fact]
        public async Task Delete_reportsOnlyTheRosterFacet_whenTheCharacterHadNothing()
        {
            await SeedRosterAsync();

            Assert.Equal(BookFacets.Characters, (await AppliedAsync(new DeleteCharacterMutation(_folder, AliceId))).Facets);
        }

        [Fact]
        public async Task Delete_takesTheVoicesAndRules_includingAForeignOne()
        {
            await SeedRosterAsync();
            var voiceId = await CommandAsync(new CreateVoiceCommand(_folder, AliceId, "Alice Voice"));
            // Bob borrows Alice's voice — Restrict FK on VoiceRules.VoiceId.
            await CommandAsync(new CreateVoiceRuleCommand(_folder, BobId, voiceId!.Value, null, null, null, null));

            var effects = await AppliedAsync(new DeleteCharacterMutation(_folder, AliceId));

            Assert.Equal(BookFacets.Characters | BookFacets.Voices | BookFacets.VoiceRules, effects.Facets);
            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Voices.AnyAsync(v => v.Id == voiceId.Value));
            Assert.False(await verify.VoiceRules.AnyAsync(r => r.VoiceId == voiceId.Value));
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == BobId));
        }

        /// <summary>
        /// Deleting the Character a Book narrates with clears the link in the same transaction —
        /// otherwise the column points at a row that no longer exists and only
        /// <see cref="NarratorIdentity"/>'s dangling-link fallback keeps the audio alive.
        /// </summary>
        [Fact]
        public async Task Delete_clearsANarratorLinkToIt_andSaysSo()
        {
            await SeedRosterAsync();
            await AppliedAsync(new SetNarratorCharacterMutation(_folder, AliceId));

            var effects = await AppliedAsync(new DeleteCharacterMutation(_folder, AliceId));

            Assert.True(effects.Facets.HasFlag(BookFacets.Narrator));
            await using var verify = await OpenDbAsync();
            // The column, not just the projection: LoadAsync would report Unlinked either way,
            // because a link to a deleted row falls back. This asserts the fallback is not what saved
            // us. (Sanctioned raw read — see NarratorCharacterIdAccessRuleTests.)
            Assert.Null(await verify.Projects.Select(p => p.NarratorCharacterId).FirstAsync());
            Assert.Equal(NarratorIdentity.Unlinked, await NarratorIdentity.LoadAsync(verify));
        }

        [Fact]
        public async Task Delete_leavesSomeoneElsesNarratorLinkAlone()
        {
            await SeedRosterAsync();
            await AppliedAsync(new SetNarratorCharacterMutation(_folder, BobId));

            var effects = await AppliedAsync(new DeleteCharacterMutation(_folder, AliceId));

            Assert.False(effects.Facets.HasFlag(BookFacets.Narrator));
            await using var verify = await OpenDbAsync();
            Assert.Equal(BobId, (await NarratorIdentity.LoadAsync(verify)).CharacterId);
        }

        [Fact]
        public async Task Delete_refusesTheSeedNarratorRow()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.Validation,
                await RefusedAsync(new DeleteCharacterMutation(_folder, ProjectDbContext.NarratorId)));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
        }

        [Fact]
        public async Task Delete_refusesACharacterThisBookDoesNotHave()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new DeleteCharacterMutation(_folder, Guid.NewGuid())));
        }

        // ── narrator link ────────────────────────────────────────────────────

        [Fact]
        public async Task SetNarrator_linksTheCharacter()
        {
            await SeedRosterAsync();

            var effects = await AppliedAsync(new SetNarratorCharacterMutation(_folder, AliceId));

            Assert.Equal(BookFacets.Narrator, effects.Facets);
            Assert.Equal(BookMutationScope.WholeProject, effects.Scope);

            await using var verify = await OpenDbAsync();
            var identity = await NarratorIdentity.LoadAsync(verify);
            Assert.Equal(AliceId, identity.CharacterId);
            Assert.Equal("Alice", identity.DisplayName);
        }

        [Fact]
        public async Task SetNarrator_unlinksOnNull()
        {
            await SeedRosterAsync();
            await AppliedAsync(new SetNarratorCharacterMutation(_folder, AliceId));

            await AppliedAsync(new SetNarratorCharacterMutation(_folder, null));

            await using var verify = await OpenDbAsync();
            Assert.Equal(NarratorIdentity.Unlinked, await NarratorIdentity.LoadAsync(verify));
        }

        [Theory]
        [InlineData(true)]   // re-picking the character already linked
        [InlineData(false)]  // unlinking a Book that was never linked
        public async Task SetNarrator_changesNothing_whenTheLinkIsAlreadyThat(bool linked)
        {
            await SeedRosterAsync();
            if (linked) await AppliedAsync(new SetNarratorCharacterMutation(_folder, AliceId));

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetNarratorCharacterMutation(_folder, linked ? AliceId : null)));
        }

        [Fact]
        public async Task SetNarrator_refusesTheSeedNarratorRow()
        {
            await SeedRosterAsync();

            // Linking the narrator to itself is nonsense: it *is* the unlinked state.
            Assert.Equal(BookMutationRejection.Validation,
                await RefusedAsync(new SetNarratorCharacterMutation(_folder, ProjectDbContext.NarratorId)));

            await using var verify = await OpenDbAsync();
            Assert.False((await NarratorIdentity.LoadAsync(verify)).IsLinked);
        }

        /// <summary>
        /// Covers the foreign-id case too: each project owns its own SQLite file, so a Character
        /// belonging to another project is simply an id this project's Characters table does not hold.
        /// </summary>
        [Fact]
        public async Task SetNarrator_refusesACharacterThisBookDoesNotHave_andLeavesTheLinkAlone()
        {
            await SeedRosterAsync();
            await AppliedAsync(new SetNarratorCharacterMutation(_folder, AliceId));

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new SetNarratorCharacterMutation(_folder, Guid.NewGuid())));

            await using var verify = await OpenDbAsync();
            Assert.Equal(AliceId, (await NarratorIdentity.LoadAsync(verify)).CharacterId);
        }

        [Fact]
        public async Task SetNarrator_refusesABookWithNoProjectRow()
        {
            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new SetNarratorCharacterMutation(_folder, null)));
        }

        // ── narrator-only mode ───────────────────────────────────────────────

        [Fact]
        public async Task NarratorOnlyMode_flipsThePolicy_andReportsItAsBookWide()
        {
            await SeedRosterAsync();

            var effects = await AppliedAsync(new SetNarratorOnlyModeMutation(_folder, true));

            Assert.Equal(BookFacets.ProjectPolicy, effects.Facets);
            Assert.Equal(BookMutationScope.WholeProject, effects.Scope);

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Projects.Select(p => p.NarratorOnlyMode).FirstAsync());
        }

        [Fact]
        public async Task NarratorOnlyMode_changesNothing_whenItIsAlreadyThatWay()
        {
            await SeedRosterAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetNarratorOnlyModeMutation(_folder, false)));
        }

        [Fact]
        public async Task NarratorOnlyMode_refusesABookWithNoProjectRow()
        {
            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new SetNarratorOnlyModeMutation(_folder, true)));
        }
    }
}
