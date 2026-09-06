using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Commands;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// What <c>POST /api/projects/{folder}/commands</c> answers for the Voice and Voice Rule family.
    /// <para>
    /// The writes themselves are proved against <c>BookMutations</c> in
    /// <see cref="Tests.Services.Mutations.VoiceLifecycleMutationTests"/>. What is left here is the one
    /// thing only the command layer decides: which of the mutations' expected refusals is flattened
    /// back to <c>200 { "newEntityId": null }</c>. Every gesture below was a silent no-op before this
    /// migration, and ADR 0007 keeps the endpoint's contract — so the refusals the mutations now state
    /// out loud must still reach an agent as null.
    /// </para>
    /// </summary>
    public class VoiceCommandContractTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public VoiceCommandContractTests()
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

        /// <summary>
        /// The wire answer: the id the command reports, having first insisted the command was not
        /// refused — a refusal is a 422 on the endpoint, not a success-shaped null id.
        /// </summary>
        private async Task<Guid?> RunAsync(BookCommand command)
        {
            await using var scope = _root.CreateAsyncScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<BookCommandDispatcher>().ExecuteAsync(command);
            Assert.False(
                result.Outcome is BookMutationOutcome.Rejected,
                $"{command.GetType().Name} was refused: {(result.Outcome as BookMutationOutcome.Rejected)?.Message}");
            return result.EntityId;
        }

        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();

        private Task SeedAsync() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .WithCharacter("bob", new Character { Id = BobId, Name = "Bob" })
                .BuildAsync();

        public static TheoryData<string> UnknownTargetGestures() =>
            ["create", "default", "update", "prompt", "transcript", "audio", "source", "delete", "delete-rule", "move-rule"];

        [Theory]
        [MemberData(nameof(UnknownTargetGestures))]
        public async Task AGestureAgainstSomethingTheBookDoesNotHave_AnswersNull(string gesture)
        {
            await SeedAsync();
            var missing = Guid.NewGuid();

            BookCommand command = gesture switch
            {
                "create" => new CreateVoiceCommand(_folder, missing, "V"),
                "default" => new SetVoiceDefaultCommand(_folder, missing),
                "update" => new UpdateVoiceCommand(_folder, missing, "V", null),
                "prompt" => new SetVoiceDesignPromptCommand(_folder, missing, "p"),
                "transcript" => new SetVoiceTranscriptCommand(_folder, missing, "t"),
                "audio" => new SetVoiceAudioCommand(_folder, missing, "voices/a/v.wav"),
                "source" => new SetVoiceSourceCommand(_folder, missing, IsGenerated: true),
                "delete" => new DeleteVoiceCommand(_folder, missing),
                "delete-rule" => new DeleteVoiceRuleCommand(_folder, missing),
                _ => new MoveVoiceRuleCommand(_folder, missing, RuleMoveDirection.Up),
            };

            Assert.Null(await RunAsync(command));
        }

        /// <summary>
        /// The three refusals this family states as validation rather than as a missing row. All of
        /// them were silent no-ops in the handlers this migration replaced, so all of them still
        /// answer null.
        /// </summary>
        [Theory]
        [InlineData("cross-character-rule")]
        [InlineData("delete-default-rule")]
        [InlineData("move-default-rule")]
        public async Task ARefusedButWellFormedGesture_AnswersNull_AndChangesNothing(string gesture)
        {
            await SeedAsync();
            var alicesVoice = await RunAsync(new CreateVoiceCommand(_folder, AliceId, "Alice Voice"));
            var bobsVoice = await RunAsync(new CreateVoiceCommand(_folder, BobId, "Bob Voice"));

            var defaultRuleId = await DefaultRuleIdAsync(AliceId);

            BookCommand command = gesture switch
            {
                "cross-character-rule" =>
                    new CreateVoiceRuleCommand(_folder, AliceId, bobsVoice!.Value, null, null, null, null),
                "delete-default-rule" => new DeleteVoiceRuleCommand(_folder, defaultRuleId),
                _ => new MoveVoiceRuleCommand(_folder, defaultRuleId, RuleMoveDirection.Down),
            };

            Assert.Null(await RunAsync(command));

            // Alice still has exactly her default rule, still pointing at her own voice.
            await using var verify = await OpenDbAsync();
            var rules = await verify.VoiceRules.Where(r => r.CharacterId == AliceId).ToListAsync();
            var rule = Assert.Single(rules);
            Assert.True(rule.IsDefault);
            Assert.Equal(alicesVoice, rule.VoiceId);
        }

        private async Task<Guid> DefaultRuleIdAsync(Guid characterId)
        {
            await using var db = await OpenDbAsync();
            return (await db.VoiceRules.FirstAsync(r => r.CharacterId == characterId && r.IsDefault)).Id;
        }
    }
}
