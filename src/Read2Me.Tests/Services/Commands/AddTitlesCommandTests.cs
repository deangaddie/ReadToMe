using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Commands;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.Commands
{
    /// <summary>
    /// Add titles over several levels as one request, so that later it can be one History step. The
    /// sweeps themselves are the title mutations', proved in
    /// <see cref="Tests.Services.Mutations.AdditiveStructuralMutationTests"/>; what is left here is
    /// which levels run, in what order, and what the one answer says.
    /// </summary>
    public class AddTitlesCommandTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public AddTitlesCommandTests()
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

        private BookHierarchyBuilder Book() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithProject(title: "A Study in Scarlet", author: "Arthur Conan Doyle")
                .AddVolume("v1", v => v
                    .AddChapter("Chapter One", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One")))
                    .AddChapter("Chapter Two", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two"))));

        private async Task<List<string?>> SpokenAsync()
        {
            await using var db = await OpenDbAsync();
            return await db.ParagraphItems.Select(i => i.Text).ToListAsync();
        }

        [Fact]
        public async Task EveryTickedLevel_LandsInOneRequest()
        {
            await Book().BuildAsync();

            var result = await RunAsync(new AddTitlesCommand(_folder, [TitleLevel.Chapter, TitleLevel.Book]));

            Assert.IsType<BookMutationOutcome.Committed>(result.Outcome);
            var spoken = await SpokenAsync();
            Assert.Contains("A Study in Scarlet", spoken);
            Assert.Contains("Chapter One", spoken);
            Assert.Contains("Chapter Two", spoken);
        }

        [Fact]
        public async Task ALevelNamedTwice_RunsOnce()
        {
            await Book().BuildAsync();

            await RunAsync(new AddTitlesCommand(_folder, [TitleLevel.Chapter, TitleLevel.Chapter]));

            Assert.Single(await SpokenAsync(), t => t == "Chapter One");
        }

        [Fact]
        public async Task LevelsWithNothingToAdd_AreNoChange()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("v1", v => v.AddChapter(null, c => c.AddParagraph("pg", g => g.AddNarration("i1", "One"))))
                .BuildAsync();

            var result = await RunAsync(new AddTitlesCommand(_folder, [TitleLevel.Part, TitleLevel.Chapter]));

            Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
        }

        [Fact]
        public async Task NoLevels_IsRefused()
        {
            await Book().BuildAsync();

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(
                (await RunAsync(new AddTitlesCommand(_folder, []))).Outcome);

            Assert.Equal(BookMutationRejection.Validation, rejected.Reason);
        }

        [Fact]
        public async Task AnUnknownLevel_IsRefused_AndRunsNothing()
        {
            await Book().BuildAsync();

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(
                (await RunAsync(new AddTitlesCommand(_folder, [TitleLevel.Chapter, (TitleLevel)9]))).Outcome);

            Assert.Equal(BookMutationRejection.Validation, rejected.Reason);
            Assert.DoesNotContain("Chapter One", await SpokenAsync());
        }

        [Theory]
        [InlineData(new[] { TitleLevel.Book }, "Book title was added.")]
        [InlineData(new[] { TitleLevel.Chapter }, "Chapter titles were added.")]
        [InlineData(new[] { TitleLevel.Book, TitleLevel.Volume, TitleLevel.Part }, "Book, volume and part titles were added.")]
        public void WhatLandedBeforeARefusal_IsNamedInTheWebsWording(TitleLevel[] landed, string expected) =>
            Assert.Equal(expected, Read2Me.Services.Commands.Handlers.AddTitlesHandler.LandedMessage([.. landed]));

        /// <summary>The book title runs first and has no project record to read: nothing after it runs.</summary>
        [Fact]
        public async Task ARefusal_StopsTheLevelsAfterIt()
        {
            await Book().AddHierarchyAsync();

            var result = await RunAsync(new AddTitlesCommand(_folder, [TitleLevel.Chapter, TitleLevel.Book]));

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(result.Outcome);
            Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);
            Assert.DoesNotContain("Chapter One", await SpokenAsync());
        }
    }
}
