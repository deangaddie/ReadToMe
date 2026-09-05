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
    /// What <c>POST /api/projects/{folder}/commands</c> answers for the Character roster family.
    /// <para>
    /// The writes themselves are proved against <c>BookMutations</c> in
    /// <see cref="Tests.Services.Mutations.CharacterLifecycleMutationTests"/>. What is left here is
    /// the one thing only the command layer decides: which of the mutations' expected refusals is
    /// flattened back to <c>200 { "newEntityId": null }</c> and which becomes a 422. These commands
    /// have always answered a protected-narrator or unknown-target gesture with null, and this
    /// migration must not have changed that (ADR 0007 keeps the endpoint's contract).
    /// </para>
    /// </summary>
    public class CharacterCommandContractTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public CharacterCommandContractTests()
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

        private Task SeedAsync() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .BuildAsync();

        public static TheoryData<string> ProtectedNarratorGestures() => ["rename", "delete", "merge"];

        [Theory]
        [MemberData(nameof(ProtectedNarratorGestures))]
        public async Task AGestureAgainstTheSeedNarratorRow_AnswersNull_AndLeavesItThere(string gesture)
        {
            await SeedAsync();
            var narrator = ProjectDbContext.NarratorId;

            BookCommand command = gesture switch
            {
                "rename" => new RenameCharacterCommand(_folder, narrator, "Voice of God"),
                "delete" => new DeleteCharacterCommand(_folder, narrator),
                _ => new MergeCharactersCommand(_folder, AliceId, narrator, false),
            };

            Assert.Null(await RunAsync(command));

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == narrator));
        }

        public static TheoryData<string> UnknownTargetGestures() => ["rename", "delete", "merge", "alias", "unalias"];

        [Theory]
        [MemberData(nameof(UnknownTargetGestures))]
        public async Task AGestureNamingSomethingTheBookDoesNotHave_AnswersNullRatherThanThrowing(string gesture)
        {
            await SeedAsync();
            var missing = Guid.NewGuid();

            BookCommand command = gesture switch
            {
                "rename" => new RenameCharacterCommand(_folder, missing, "Nobody"),
                "delete" => new DeleteCharacterCommand(_folder, missing),
                "merge" => new MergeCharactersCommand(_folder, AliceId, missing, false),
                "alias" => new AddCharacterAliasCommand(_folder, missing, "Ally"),
                _ => new RemoveCharacterAliasCommand(_folder, missing),
            };

            Assert.Null(await RunAsync(command));
        }

        /// <summary>
        /// The one command in the family that returns an id, and the one whose answer a discovery run
        /// applied twice depends on: creating a name the roster already answers to creates nobody and
        /// still names whoever does.
        /// </summary>
        [Fact]
        public async Task CreateCharacter_AnswersTheExistingId_WhenTheNameAlreadyResolves()
        {
            await SeedAsync();

            Assert.Equal(AliceId, await RunAsync(new CreateCharacterCommand(_folder, "alice")));

            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.Characters.CountAsync(c => c.Name == "Alice"));
        }

        [Fact]
        public async Task CreateCharacter_AnswersTheNewId_WhenNobodyGoesByTheName()
        {
            await SeedAsync();

            var created = await RunAsync(new CreateCharacterCommand(_folder, "Carol"));

            await using var verify = await OpenDbAsync();
            Assert.Equal((await verify.Characters.SingleAsync(c => c.Name == "Carol")).Id, created);
        }

        /// <summary>
        /// A gesture that legally changes nothing answers null too — indistinguishable from a refusal
        /// on the wire, which is exactly the flattening this family has always done.
        /// </summary>
        [Fact]
        public async Task AGestureThatChangesNothing_AnswersNull()
        {
            await SeedAsync();

            Assert.Null(await RunAsync(new RenameCharacterCommand(_folder, AliceId, "Alice")));
        }
    }
}
