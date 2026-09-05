using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Api;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Commands;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Api
{
    /// <summary>
    /// <c>POST /api/projects/{folder}/commands</c> driven end to end over a real project, from the
    /// deserialized command down to the answer the endpoint returns. ADR 0007 moved every command
    /// family onto <see cref="BookMutations"/>; what an agent sees did not move with it, and these
    /// are the cases that would notice if it had.
    /// </summary>
    public class BookCommandApiContractTests : ProjectDbTestBase
    {
        private readonly ProjectFolderId _folder;
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _request;
        private readonly BookCommandApiAdapter _api;
        private readonly EventBroadcaster<BookMutationReceipt> _receipts;

        public BookCommandApiContractTests()
        {
            _folder = new ProjectFolderId(FolderName);

            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();

            // One API request's scope, which is not the scope any Book View lives in.
            _request = _root.CreateAsyncScope();
            _api = new BookCommandApiAdapter(_request.ServiceProvider.GetRequiredService<BookCommandDispatcher>());
            _receipts = _root.GetRequiredService<EventBroadcaster<BookMutationReceipt>>();
        }

        public override async ValueTask DisposeAsync()
        {
            await _request.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private async Task<IResult> PostAsync(BookCommand command) =>
            await _api.ExecuteAsync(command, CancellationToken.None);

        private static Guid? IdOf(IResult result) =>
            Assert.IsType<Ok<CommandResponse>>(result).Value!.NewEntityId;

        private static void Assert422(IResult result) =>
            Assert.Equal(
                StatusCodes.Status422UnprocessableEntity,
                Assert.IsType<ProblemHttpResult>(result).StatusCode);

        /// <summary>One volume, one chapter, one paragraph of narration.</summary>
        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                    .AddChapter("ch1", c => c.AddParagraph("p1", p => p.AddNarration("i1", "Once upon a time."))))
                .BuildAsync();
            return b;
        }

        [Fact]
        public async Task ACommandThatCreatesSomething_AnswersWithItsIdentity()
        {
            var b = await SeedAsync();

            var created = IdOf(await PostAsync(
                new InsertParagraphItemCommand(_folder, b.ItemId("i1"), InsertPosition.After, "And then.")));

            Assert.NotNull(created);
            await using var verify = await OpenDbAsync();
            Assert.NotNull(await verify.ParagraphItems.FindAsync(created));
        }

        /// <summary>
        /// The endpoint's long-standing shape for a gesture aimed at something the Book does not
        /// contain: a quiet success, not an error. The mutation now reports <c>NotFound</c>, and the
        /// command it came from is what decides that has always answered null.
        /// </summary>
        [Fact]
        public async Task ACommandNamingSomethingTheBookDoesNotContain_IsAQuietSuccess()
        {
            await SeedAsync();

            Assert.Null(IdOf(await PostAsync(new DeleteChapterCommand(_folder, Guid.NewGuid()))));
            Assert.Null(IdOf(await PostAsync(new UpdateChapterTitleCommand(_folder, Guid.NewGuid(), "X"))));
        }

        /// <summary>The other half: a refusal that has always been an error stays one.</summary>
        [Fact]
        public async Task ARefusalTheCommandHasNeverSoftened_IsAnError()
        {
            await SeedAsync();

            Assert422(await PostAsync(new SetNarratorCharacterCommand(_folder, Guid.NewGuid())));
        }

        [Fact]
        public async Task AnIllegalRequest_IsAnError()
        {
            var b = await SeedAsync();

            Assert422(await PostAsync(
                new InsertParagraphItemCommand(_folder, b.ItemId("i1"), InsertPosition.After, "   ")));
        }

        /// <summary>A valid command that changes nothing is a success that creates no revision.</summary>
        [Fact]
        public async Task ACommandThatChangesNothing_IsASuccessWithNoIdentity()
        {
            var b = await SeedAsync();
            await PostAsync(new UpdateChapterTitleCommand(_folder, b.ChapterId("ch1"), "Chapter One"));

            var receipts = 0;
            _receipts.Event += _ => receipts++;

            Assert.Null(IdOf(await PostAsync(
                new UpdateChapterTitleCommand(_folder, b.ChapterId("ch1"), "Chapter One"))));
            Assert.Equal(0, receipts);
        }

        /// <summary>
        /// <c>CreateCharacter</c> is idempotent by name — a discovery run applied twice must not
        /// double the roster, and the second call still answers with the id.
        /// </summary>
        [Fact]
        public async Task CreateCharacter_AnswersWithTheSameIdentityTwice()
        {
            await SeedAsync();

            var first = IdOf(await PostAsync(new CreateCharacterCommand(_folder, "Watson")));
            var second = IdOf(await PostAsync(new CreateCharacterCommand(_folder, "watson")));

            Assert.NotNull(first);
            Assert.Equal(first, second);
        }

        /// <summary>
        /// The pause insertion creates a Paragraph and the receipt says so, because a Book View
        /// reconciling from it needs the identity. The wire has never reported one, and does not
        /// start now.
        /// </summary>
        [Fact]
        public async Task ACommandThatHasNeverReportedWhatItCreates_StillReportsNothing()
        {
            var b = await SeedAsync();
            BookMutationReceipt? receipt = null;
            _receipts.Event += r => receipt = r;

            Assert.Null(IdOf(await PostAsync(new InsertPauseParagraphCommand(
                _folder, b.ItemId("i1"), InsertPosition.After, PauseKind.ParagraphPause))));

            Assert.NotNull(receipt!.Effects.CreatedId);
        }

        /// <summary>
        /// The receipt an API command publishes is the same factual receipt the same operation
        /// publishes from inside the app — same mutation identity, same facets, same scope — which is
        /// what lets every open Book View reconcile from it without knowing who wrote.
        /// </summary>
        [Fact]
        public async Task ACommittedCommand_PublishesTheSameReceiptAsTheInAppProducer()
        {
            var b = await SeedAsync();
            var receipts = new List<BookMutationReceipt>();
            _receipts.Event += receipts.Add;

            await PostAsync(new UpdateChapterTitleCommand(_folder, b.ChapterId("ch1"), "From the API"));

            // The same operation as the Book View's own producer commits it: one mutation, one
            // circuit-scoped BookMutations, no command in sight.
            await using var circuit = _root.CreateAsyncScope();
            await circuit.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(
                new UpdateChapterTitleMutation(_folder, b.ChapterId("ch1"), "From the Book View"));

            Assert.Equal(2, receipts.Count);
            var (fromApi, fromApp) = (receipts[0], receipts[1]);
            Assert.Equal(fromApp.MutationName, fromApi.MutationName);
            Assert.Equal(fromApp.FolderId, fromApi.FolderId);
            Assert.Equal(fromApp.Effects.Facets, fromApi.Effects.Facets);
            Assert.Equal(fromApp.Effects.Scope, fromApi.Effects.Scope);
            Assert.Equal(fromApp.Effects.NodeIds, fromApi.Effects.NodeIds);

            // Distinct revisions in commit order, so a reader can tell the two apart and cannot
            // regress onto the older one.
            Assert.True(fromApp.Revision > fromApi.Revision);
        }
    }
}
