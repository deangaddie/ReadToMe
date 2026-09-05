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
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Services.Mutations
{
    /// <summary>
    /// The Voice and Voice Rule family proved through <see cref="BookMutations.CommitAsync"/> against
    /// a real SQLite project (ADR 0007).
    /// <para>
    /// Three things matter beyond the lifecycle rules. The default Voice Rule invariant — a Character
    /// with Voices has exactly one, at the floor Rank — must survive every gesture here, including the
    /// delete that takes its target away. The receipt must report the facets this Book actually moved,
    /// so a delete that disturbed no rule says <see cref="BookFacets.Voices"/> alone while one that
    /// repointed the fallback says both. And a gesture that writes nothing must be
    /// <see cref="BookMutationOutcome.NoChange"/>, so that saving an unedited form costs no revision
    /// and no reconciliation in any open Book View.
    /// </para>
    /// </summary>
    public class VoiceLifecycleMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public VoiceLifecycleMutationTests()
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

        // ── harness ──────────────────────────────────────────────────────────

        /// <summary>Commits in its own scope, the way a producer does, and returns the outcome.</summary>
        private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
        }

        private async Task<BookMutationEffects> AppliedAsync(BookMutation mutation) =>
            Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(mutation)).Receipt.Effects;

        private async Task<Guid> CreatedAsync(BookMutation mutation) =>
            (await AppliedAsync(mutation)).CreatedId!.Value;

        private async Task<BookMutationRejection> RefusedAsync(BookMutation mutation) =>
            Assert.IsType<BookMutationOutcome.Rejected>(await CommitAsync(mutation)).Reason;

        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();

        /// <summary>Two characters, no Voices — every gesture here starts from a roster.</summary>
        private Task SeedRosterAsync() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .WithCharacter("bob", new Character { Id = BobId, Name = "Bob" })
                .BuildAsync();

        /// <summary>Alice with two Voices, the first of which the default rule points at.</summary>
        private async Task<(Guid First, Guid Second)> SeedTwoVoicesAsync()
        {
            await SeedRosterAsync();
            var first = await CreatedAsync(new CreateVoiceMutation(_folder, AliceId, "V1"));
            var second = await CreatedAsync(new CreateVoiceMutation(_folder, AliceId, "V2"));
            return (first, second);
        }

        private async Task<VoiceEntity?> ReadVoiceAsync(Guid voiceId)
        {
            await using var db = await OpenDbAsync();
            return await db.Voices.FirstOrDefaultAsync(v => v.Id == voiceId);
        }

        private async Task<List<VoiceRule>> ReadRulesAsync(Guid characterId)
        {
            await using var db = await OpenDbAsync();
            return await db.VoiceRules
                .Where(r => r.CharacterId == characterId)
                .OrderBy(r => r.Rank)
                .ToListAsync();
        }

        private async Task<VoiceRule?> ReadDefaultRuleAsync(Guid characterId) =>
            (await ReadRulesAsync(characterId)).FirstOrDefault(r => r.IsDefault);

        // ── creating a Voice ─────────────────────────────────────────────────

        [Fact]
        public async Task CreateVoice_FirstVoice_CreatesTheDefaultRuleAtTheFloorRank()
        {
            await SeedRosterAsync();

            var voiceId = await CreatedAsync(new CreateVoiceMutation(_folder, AliceId, "Alice Voice"));

            var rule = Assert.Single(await ReadRulesAsync(AliceId));
            Assert.True(rule.IsDefault);
            Assert.Equal(voiceId, rule.VoiceId);
            Assert.Equal("a0", rule.Rank);
            Assert.Null(rule.FromLevel);
            Assert.Null(rule.FromNodeId);
            Assert.Null(rule.ToLevel);
            Assert.Null(rule.ToNodeId);
        }

        [Fact]
        public async Task CreateVoice_SecondVoice_LeavesTheDefaultRuleWhereItIs()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            var rule = Assert.Single(await ReadRulesAsync(AliceId));
            Assert.True(rule.IsDefault);
            Assert.Equal(first, rule.VoiceId);
        }

        /// <summary>
        /// The receipt says what this Book moved: the first Voice brought a rule with it, the second
        /// did not, and a reader reconciles from that rather than from the mutation's name.
        /// </summary>
        [Fact]
        public async Task CreateVoice_ReportsTheVoiceRuleFacetOnlyWhenItMadeOne()
        {
            await SeedRosterAsync();

            var first = await AppliedAsync(new CreateVoiceMutation(_folder, AliceId, "V1"));
            Assert.Equal(BookFacets.Voices | BookFacets.VoiceRules, first.Facets);

            var second = await AppliedAsync(new CreateVoiceMutation(_folder, AliceId, "V2"));
            Assert.Equal(BookFacets.Voices, second.Facets);
        }

        [Fact]
        public async Task CreateVoice_EmptyName_TakesTheCharactersName()
        {
            await SeedRosterAsync();

            var voiceId = await CreatedAsync(new CreateVoiceMutation(_folder, BobId, ""));

            Assert.Equal("Bob", (await ReadVoiceAsync(voiceId))!.Name);
        }

        /// <summary>
        /// The batch's shape: a planned Voice arrives complete, in one commit, rather than as a name
        /// then a description then a prompt.
        /// </summary>
        [Fact]
        public async Task CreateVoice_WithDescriptionAndPrompt_LandsThemInTheSameCommit()
        {
            await SeedRosterAsync();

            var voiceId = await CreatedAsync(new CreateVoiceMutation(
                _folder, AliceId, "Young Alice", IsGenerated: true, "Part 1", "a girl's voice"));

            var voice = await ReadVoiceAsync(voiceId);
            Assert.Equal("Part 1", voice!.Description);
            Assert.Equal("a girl's voice", voice.DesignPrompt);
            Assert.Equal(VoiceSource.Generated, voice.Source);
        }

        /// <summary>
        /// Unlike a Character, a Voice is not deduplicated by name: two takes of one person are two
        /// recordings, and calling them the same thing is a label rather than a claim.
        /// </summary>
        [Fact]
        public async Task CreateVoice_SameNameTwice_MakesTwoVoices()
        {
            await SeedRosterAsync();

            var first = await CreatedAsync(new CreateVoiceMutation(_folder, AliceId, "Alice"));
            var second = await CreatedAsync(new CreateVoiceMutation(_folder, AliceId, "Alice"));

            Assert.NotEqual(first, second);
        }

        [Fact]
        public async Task CreateVoice_ForACharacterTheBookDoesNotHave_IsNotFound()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new CreateVoiceMutation(_folder, Guid.NewGuid(), "V")));
        }

        // ── the default Voice ────────────────────────────────────────────────

        [Fact]
        public async Task SetVoiceDefault_RepointsTheOneRuleRatherThanAddingAnother()
        {
            var (_, second) = await SeedTwoVoicesAsync();

            var effects = await AppliedAsync(new SetVoiceDefaultMutation(_folder, second));

            Assert.Equal(BookFacets.VoiceRules, effects.Facets);
            var rule = Assert.Single(await ReadRulesAsync(AliceId));
            Assert.True(rule.IsDefault);
            Assert.Equal(second, rule.VoiceId);
        }

        [Fact]
        public async Task SetVoiceDefault_ToTheVoiceItAlreadyNames_ChangesNothing()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetVoiceDefaultMutation(_folder, first)));
        }

        [Fact]
        public async Task SetVoiceDefault_ForAVoiceTheBookDoesNotHave_IsNotFound()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new SetVoiceDefaultMutation(_folder, Guid.NewGuid())));
        }

        // ── editing a Voice ──────────────────────────────────────────────────

        [Fact]
        public async Task UpdateVoice_RewritesNameAndDescription()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            var effects = await AppliedAsync(new UpdateVoiceMutation(_folder, first, "Renamed", "Gruff"));

            Assert.Equal(BookFacets.Voices, effects.Facets);
            var voice = await ReadVoiceAsync(first);
            Assert.Equal("Renamed", voice!.Name);
            Assert.Equal("Gruff", voice.Description);
        }

        [Fact]
        public async Task UpdateVoice_ToTheNameAndDescriptionItHas_ChangesNothing()
        {
            var (first, _) = await SeedTwoVoicesAsync();
            await CommitAsync(new UpdateVoiceMutation(_folder, first, "Renamed", "Gruff"));

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new UpdateVoiceMutation(_folder, first, "Renamed", "Gruff")));
        }

        [Fact]
        public async Task SetVoiceDesignPrompt_StoresTheDescriptionItIsSynthesisedFrom()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            await AppliedAsync(new SetVoiceDesignPromptMutation(_folder, first, "A gruff old man."));

            Assert.Equal("A gruff old man.", (await ReadVoiceAsync(first))!.DesignPrompt);
            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetVoiceDesignPromptMutation(_folder, first, "A gruff old man.")));
        }

        [Fact]
        public async Task SetVoiceTtsSettingsOverride_StoresThenClearsTheOverride()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            await AppliedAsync(new SetVoiceTtsSettingsOverrideMutation(_folder, first, "{\"cfg_value\":3.5}"));
            Assert.Equal("{\"cfg_value\":3.5}", (await ReadVoiceAsync(first))!.TtsSettingsOverrideJson);

            await AppliedAsync(new SetVoiceTtsSettingsOverrideMutation(_folder, first, null));
            Assert.Null((await ReadVoiceAsync(first))!.TtsSettingsOverrideJson);
        }

        [Fact]
        public async Task SetVoiceDesignSettingsOverride_StoresThenClearsTheOverride()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            await AppliedAsync(new SetVoiceDesignSettingsOverrideMutation(_folder, first, "{\"cfg\":1}"));
            Assert.Equal("{\"cfg\":1}", (await ReadVoiceAsync(first))!.VoiceDesignSettingsOverrideJson);

            await AppliedAsync(new SetVoiceDesignSettingsOverrideMutation(_folder, first, null));
            Assert.Null((await ReadVoiceAsync(first))!.VoiceDesignSettingsOverrideJson);
        }

        [Fact]
        public async Task SetVoiceTranscript_StoresWhatTheReferenceAudioSays()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            await AppliedAsync(new SetVoiceTranscriptMutation(_folder, first, "Hello there."));

            Assert.Equal("Hello there.", (await ReadVoiceAsync(first))!.Transcript);
            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetVoiceTranscriptMutation(_folder, first, "Hello there.")));
        }

        /// <summary>
        /// Re-uploading a Voice's audio writes the same path over different bytes, so this is the one
        /// Voice write that reports a change whether or not the column moved — the path is a name, not
        /// the artifact.
        /// </summary>
        [Fact]
        public async Task SetVoiceAudio_ToThePathItAlreadyNames_IsStillAChange()
        {
            var (first, _) = await SeedTwoVoicesAsync();
            await AppliedAsync(new SetVoiceAudioMutation(_folder, first, "voices/a/v.wav"));

            var again = await AppliedAsync(new SetVoiceAudioMutation(_folder, first, "voices/a/v.wav"));

            Assert.Equal(BookFacets.Voices, again.Facets);
            Assert.Equal("voices/a/v.wav", (await ReadVoiceAsync(first))!.AudioFileName);
        }

        [Fact]
        public async Task SetVoiceGenerated_RecordsAudioTranscriptAndPromptTogether()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            await AppliedAsync(new SetVoiceGeneratedMutation(
                _folder, first, "voices/a/gen.wav", "sample text", "a warm voice"));

            var voice = await ReadVoiceAsync(first);
            Assert.Equal("voices/a/gen.wav", voice!.AudioFileName);
            Assert.Equal("sample text", voice.Transcript);
            Assert.Equal("a warm voice", voice.DesignPrompt);
        }

        // ── switching a Voice's source ───────────────────────────────────────

        [Fact]
        public async Task SetVoiceSource_ToUploaded_DropsTheDesignPrompt()
        {
            var (first, _) = await SeedTwoVoicesAsync();
            await CommitAsync(new SetVoiceDesignPromptMutation(_folder, first, "a warm voice"));

            await AppliedAsync(new SetVoiceSourceMutation(_folder, first, IsGenerated: false));

            var voice = await ReadVoiceAsync(first);
            Assert.Equal(VoiceSource.Uploaded, voice!.Source);
            Assert.Null(voice.DesignPrompt);
        }

        /// <summary>
        /// There is nothing left to clone from, so the Voice stops naming its recording. The file
        /// itself goes afterwards, through the remover — see <c>VoiceAudioWriterTests</c> for why the
        /// two cannot happen in one step.
        /// </summary>
        [Fact]
        public async Task SetVoiceSource_ToGenerated_StopsNamingTheRecording()
        {
            var (first, _) = await SeedTwoVoicesAsync();
            await AppliedAsync(new SetVoiceAudioMutation(_folder, first, "voices/a/v.wav"));

            var effects = await AppliedAsync(new SetVoiceSourceMutation(_folder, first, IsGenerated: true));

            Assert.Equal(BookFacets.Voices, effects.Facets);
            var voice = await ReadVoiceAsync(first);
            Assert.Equal(VoiceSource.Generated, voice!.Source);
            Assert.Null(voice.AudioFileName);
        }

        [Fact]
        public async Task SetVoiceSource_ToTheSourceItAlreadyHasWithNothingToDrop_ChangesNothing()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            // Seeded voices are Uploaded and carry no design prompt.
            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetVoiceSourceMutation(_folder, first, IsGenerated: false)));
        }

        // ── deleting a Voice ─────────────────────────────────────────────────

        [Fact]
        public async Task DeleteVoice_NotTheDefaultTarget_LeavesTheDefaultRuleAlone()
        {
            var (first, second) = await SeedTwoVoicesAsync();

            var effects = await AppliedAsync(new DeleteVoiceMutation(_folder, second));

            // No rule named the deleted voice, so no Voice Rule facet to report.
            Assert.Equal(BookFacets.Voices, effects.Facets);
            Assert.Equal(first, (await ReadDefaultRuleAsync(AliceId))!.VoiceId);
        }

        [Fact]
        public async Task DeleteVoice_TheDefaultTarget_RepointsToTheOldestRemaining()
        {
            var (first, second) = await SeedTwoVoicesAsync();

            var effects = await AppliedAsync(new DeleteVoiceMutation(_folder, first));

            Assert.Equal(BookFacets.Voices | BookFacets.VoiceRules, effects.Facets);
            Assert.Equal(second, (await ReadDefaultRuleAsync(AliceId))!.VoiceId);
        }

        [Fact]
        public async Task DeleteVoice_TheLastVoice_TakesTheDefaultRuleWithIt()
        {
            await SeedRosterAsync();
            var only = await CreatedAsync(new CreateVoiceMutation(_folder, AliceId, "V"));

            await AppliedAsync(new DeleteVoiceMutation(_folder, only));

            Assert.Empty(await ReadRulesAsync(AliceId));
        }

        [Fact]
        public async Task DeleteVoice_CascadesThePositionalRulesThatNamedIt()
        {
            var (first, second) = await SeedTwoVoicesAsync();
            var ruleId = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, first, null, null, null, null));

            await AppliedAsync(new DeleteVoiceMutation(_folder, first));

            var rules = await ReadRulesAsync(AliceId);
            Assert.DoesNotContain(rules, r => r.Id == ruleId);
            Assert.Equal(second, Assert.Single(rules).VoiceId);
        }

        [Fact]
        public async Task DeleteVoice_ForAVoiceTheBookDoesNotHave_IsNotFound()
        {
            await SeedRosterAsync();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new DeleteVoiceMutation(_folder, Guid.NewGuid())));
        }

        // ── positional Voice Rules ───────────────────────────────────────────

        [Fact]
        public async Task CreateVoiceRule_AppendsBelowEveryRuleTheCharacterHas()
        {
            var (first, second) = await SeedTwoVoicesAsync();

            var ruleA = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, first, null, null, null, null));
            var ruleB = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, second, null, null, null, null));

            var rules = await ReadRulesAsync(AliceId);
            Assert.Equal(3, rules.Count);
            Assert.True(rules[0].IsDefault);
            Assert.Equal([ruleA, ruleB], rules.Skip(1).Select(r => r.Id));
        }

        [Fact]
        public async Task CreateVoiceRule_KeepsItsAnchors()
        {
            var (first, _) = await SeedTwoVoicesAsync();
            var chapterId = Guid.NewGuid();

            var ruleId = await CreatedAsync(new CreateVoiceRuleMutation(
                _folder, AliceId, first, VoiceAnchorLevel.Chapter, chapterId, VoiceAnchorLevel.Chapter, chapterId));

            var rule = (await ReadRulesAsync(AliceId)).Single(r => r.Id == ruleId);
            Assert.Equal(VoiceAnchorLevel.Chapter, rule.FromLevel);
            Assert.Equal(chapterId, rule.FromNodeId);
            Assert.Equal(VoiceAnchorLevel.Chapter, rule.ToLevel);
            Assert.Equal(chapterId, rule.ToNodeId);
        }

        /// <summary>
        /// A rule is a claim about which of <em>this</em> Character's Voices reads a stretch of the
        /// Book, so another Character's recording is refused rather than quietly ignored.
        /// </summary>
        [Fact]
        public async Task CreateVoiceRule_PointedAtAnotherCharactersVoice_IsRefused()
        {
            await SeedRosterAsync();
            var bobsVoice = await CreatedAsync(new CreateVoiceMutation(_folder, BobId, "Bob Voice"));

            Assert.Equal(BookMutationRejection.Validation,
                await RefusedAsync(new CreateVoiceRuleMutation(
                    _folder, AliceId, bobsVoice, null, null, null, null)));

            Assert.Empty(await ReadRulesAsync(AliceId));
        }

        [Fact]
        public async Task DeleteVoiceRule_RemovesThePositionalRule()
        {
            var (first, _) = await SeedTwoVoicesAsync();
            var ruleId = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, first, null, null, null, null));

            var effects = await AppliedAsync(new DeleteVoiceRuleMutation(_folder, ruleId));

            Assert.Equal(BookFacets.VoiceRules, effects.Facets);
            Assert.DoesNotContain(await ReadRulesAsync(AliceId), r => r.Id == ruleId);
        }

        /// <summary>
        /// The default rule is the fallback every position lands on, so deleting or reordering it is
        /// refused rather than ignored — the old handlers answered both with a silent nothing.
        /// </summary>
        [Theory]
        [InlineData("delete")]
        [InlineData("move")]
        public async Task AGestureAgainstTheDefaultRule_IsRefused(string gesture)
        {
            var (_, _) = await SeedTwoVoicesAsync();
            var defaultRule = (await ReadDefaultRuleAsync(AliceId))!;

            BookMutation mutation = gesture == "delete"
                ? new DeleteVoiceRuleMutation(_folder, defaultRule.Id)
                : new MoveVoiceRuleMutation(_folder, defaultRule.Id, RuleMoveDirection.Down);

            Assert.Equal(BookMutationRejection.Validation, await RefusedAsync(mutation));
            Assert.NotNull(await ReadDefaultRuleAsync(AliceId));
        }

        [Fact]
        public async Task VoiceRuleGestures_AgainstARuleTheBookDoesNotHave_AreNotFound()
        {
            await SeedRosterAsync();
            var missing = Guid.NewGuid();

            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new DeleteVoiceRuleMutation(_folder, missing)));
            Assert.Equal(BookMutationRejection.NotFound,
                await RefusedAsync(new MoveVoiceRuleMutation(_folder, missing, RuleMoveDirection.Up)));
        }

        [Fact]
        public async Task MoveVoiceRule_Up_SwapsWithItsPredecessor()
        {
            var (first, second) = await SeedTwoVoicesAsync();
            var ruleA = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, first, null, null, null, null));
            var ruleB = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, second, null, null, null, null));

            await AppliedAsync(new MoveVoiceRuleMutation(_folder, ruleB, RuleMoveDirection.Up));

            var positional = (await ReadRulesAsync(AliceId)).Where(r => !r.IsDefault).Select(r => r.Id);
            Assert.Equal([ruleB, ruleA], positional);
        }

        [Fact]
        public async Task MoveVoiceRule_Down_SwapsWithItsSuccessor()
        {
            var (first, second) = await SeedTwoVoicesAsync();
            var ruleA = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, first, null, null, null, null));
            var ruleB = await CreatedAsync(
                new CreateVoiceRuleMutation(_folder, AliceId, second, null, null, null, null));

            await AppliedAsync(new MoveVoiceRuleMutation(_folder, ruleA, RuleMoveDirection.Down));

            var positional = (await ReadRulesAsync(AliceId)).Where(r => !r.IsDefault).Select(r => r.Id);
            Assert.Equal([ruleB, ruleA], positional);
        }

        /// <summary>
        /// A rule at the end of the direction asked for is a legal gesture at its limit, not a
        /// refusal — and the default rule's floor Rank is the top the first positional rule stops at.
        /// </summary>
        [Theory]
        [InlineData(RuleMoveDirection.Up, 0)]
        [InlineData(RuleMoveDirection.Down, 1)]
        public async Task MoveVoiceRule_AtItsLimit_ChangesNothing(RuleMoveDirection direction, int index)
        {
            var (first, second) = await SeedTwoVoicesAsync();
            Guid[] rules =
            [
                await CreatedAsync(new CreateVoiceRuleMutation(_folder, AliceId, first, null, null, null, null)),
                await CreatedAsync(new CreateVoiceRuleMutation(_folder, AliceId, second, null, null, null, null)),
            ];

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new MoveVoiceRuleMutation(_folder, rules[index], direction)));

            var positional = (await ReadRulesAsync(AliceId)).Where(r => !r.IsDefault).Select(r => r.Id);
            Assert.Equal(rules, positional);
        }

        /// <summary>
        /// A rule may be anchored at a node the Book no longer has. The write side does not police
        /// that — <see cref="Read2Me.Services.Voice.VoiceResolver"/> already falls a dangling rule
        /// back to the default, and refusing the write instead would make a rule undeletable after a
        /// chapter it named was merged away.
        /// </summary>
        [Fact]
        public async Task CreateVoiceRule_AnchoredAtANodeTheBookDoesNotHave_IsStillCreated()
        {
            var (first, _) = await SeedTwoVoicesAsync();

            var ruleId = await CreatedAsync(new CreateVoiceRuleMutation(
                _folder, AliceId, first, VoiceAnchorLevel.Chapter, Guid.NewGuid(), null, null));

            Assert.Contains(await ReadRulesAsync(AliceId), r => r.Id == ruleId);
        }
    }
}
