using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Commands;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// "New character…" from the speaker menu as one command: resolve-or-create the name, then stamp
    /// it on exactly one target (an item, a paragraph, or a selection of paragraphs). One request, so
    /// that later it can be one History step. The stamping rules themselves are the speaker
    /// mutations', proved in <see cref="Tests.Services.Mutations.SpeakerAttributionMutationTests"/>.
    /// </summary>
    public class CreateCharacterAndAssignCommandTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;
        private BookHierarchyBuilder _book = null!;

        private static readonly Guid AliceId = Guid.NewGuid();

        public CreateCharacterAndAssignCommandTests()
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

        private async Task<BookCommandResult> RunAsync(BookCommand command)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BookCommandDispatcher>().ExecuteAsync(command);
        }

        /// <summary>Two paragraphs: an unattributed line plus narration, then one more unattributed line.</summary>
        private async Task SeedAsync()
        {
            _book = new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" });
            await _book.AddVolume("v", v => v.AddChapter(configure: c => c
                .AddParagraph("p1", p => p
                    .AddRawItem("i1", ParagraphItemType.Speech, "\"Hello,\" ")
                    .AddNarration("n1", "she said."))
                .AddParagraph("p2", p => p
                    .AddRawItem("i2", ParagraphItemType.Speech, "\"Goodbye.\"")))).BuildAsync();
        }

        private async Task<Guid?> SpeakerAsync(string item)
        {
            await using var db = await OpenDbAsync();
            return (await db.ParagraphItems.SingleAsync(i => i.Id == _book.ItemId(item))).CharacterId;
        }

        private async Task<Guid> CharacterNamedAsync(string name)
        {
            await using var db = await OpenDbAsync();
            return (await db.Characters.SingleAsync(c => c.Name == name)).Id;
        }

        [Fact]
        public async Task OnAnItem_CreatesTheCharacter_AndStampsTheItem_AnsweringItsId()
        {
            await SeedAsync();

            var result = await RunAsync(new CreateCharacterAndAssignCommand(
                _folder, "Carol", ItemId: _book.ItemId("i1")));

            Assert.IsType<BookMutationOutcome.Committed>(result.Outcome);
            var carol = await CharacterNamedAsync("Carol");
            Assert.Equal(carol, result.EntityId);
            Assert.Equal(carol, await SpeakerAsync("i1"));
            Assert.Null(await SpeakerAsync("i2"));
        }

        [Fact]
        public async Task OnAParagraph_StampsItsDialog_AndLeavesItsNarration()
        {
            await SeedAsync();

            var result = await RunAsync(new CreateCharacterAndAssignCommand(
                _folder, "Carol", ParagraphId: _book.ParagraphId("p1")));

            Assert.IsType<BookMutationOutcome.Committed>(result.Outcome);
            var carol = await CharacterNamedAsync("Carol");
            Assert.Equal(carol, await SpeakerAsync("i1"));
            Assert.Equal(ProjectDbContext.NarratorId, await SpeakerAsync("n1"));
        }

        [Fact]
        public async Task OnASelection_StampsEveryListedParagraph()
        {
            await SeedAsync();

            var result = await RunAsync(new CreateCharacterAndAssignCommand(
                _folder, "Carol", ParagraphIds: [_book.ParagraphId("p1"), _book.ParagraphId("p2")]));

            Assert.IsType<BookMutationOutcome.Committed>(result.Outcome);
            var carol = await CharacterNamedAsync("Carol");
            Assert.Equal(carol, await SpeakerAsync("i1"));
            Assert.Equal(carol, await SpeakerAsync("i2"));
        }

        /// <summary>Idempotent by name like <c>CreateCharacter</c>: an existing name or alias is assigned, not duplicated.</summary>
        [Fact]
        public async Task ANameTheRosterAlreadyAnswersTo_AssignsThatCharacter_AndCreatesNobody()
        {
            await SeedAsync();

            var result = await RunAsync(new CreateCharacterAndAssignCommand(
                _folder, " alice ", ItemId: _book.ItemId("i1")));

            Assert.IsType<BookMutationOutcome.Committed>(result.Outcome);
            Assert.Equal(AliceId, result.EntityId);
            Assert.Equal(AliceId, await SpeakerAsync("i1"));
            await using var db = await OpenDbAsync();
            Assert.Equal(1, await db.Characters.CountAsync(c => c.Name == "Alice"));
        }

        /// <summary>An existing speaker already stamped there: nothing to write, but the id still answers.</summary>
        [Fact]
        public async Task AnAssignThatChangesNothing_IsNoChange_AndStillAnswersTheId()
        {
            await SeedAsync();
            await RunAsync(new SetItemCharacterCommand(_folder, _book.ItemId("i1"), AliceId));

            var result = await RunAsync(new CreateCharacterAndAssignCommand(
                _folder, "Alice", ItemId: _book.ItemId("i1")));

            Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
            Assert.Equal(AliceId, result.EntityId);
        }

        public static TheoryData<string> MalformedRequests() => ["blank name", "no target", "two targets", "empty selection"];

        [Theory]
        [MemberData(nameof(MalformedRequests))]
        public async Task AMalformedRequest_IsRefused_AndCreatesNobody(string shape)
        {
            await SeedAsync();
            var item = _book.ItemId("i1");
            var paragraph = _book.ParagraphId("p1");

            var command = shape switch
            {
                "blank name" => new CreateCharacterAndAssignCommand(_folder, "  ", ItemId: item),
                "no target" => new CreateCharacterAndAssignCommand(_folder, "Carol"),
                "two targets" => new CreateCharacterAndAssignCommand(_folder, "Carol", ItemId: item, ParagraphId: paragraph),
                _ => new CreateCharacterAndAssignCommand(_folder, "Carol", ParagraphIds: []),
            };

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>((await RunAsync(command)).Outcome);
            Assert.Equal(BookMutationRejection.Validation, rejected.Reason);
            await using var db = await OpenDbAsync();
            Assert.False(await db.Characters.AnyAsync(c => c.Name == "Carol"));
        }

        /// <summary>
        /// Unlike <c>SetItemCharacter</c>, a target the Book does not contain is a 422, not a null:
        /// the character is created either way, so the answer must say the assign did not happen.
        /// </summary>
        [Fact]
        public async Task AnUnknownTarget_IsRefused()
        {
            await SeedAsync();

            var result = await RunAsync(new CreateCharacterAndAssignCommand(_folder, "Carol", ItemId: Guid.NewGuid()));

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(result.Outcome);
            Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);
        }
    }
}
