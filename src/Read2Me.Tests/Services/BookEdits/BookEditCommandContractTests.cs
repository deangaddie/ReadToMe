using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    /// <summary>
    /// What <c>POST /api/projects/{folder}/commands</c> answers for the title, item text and AI edit
    /// commands.
    /// <para>
    /// The writes themselves are proved against <c>BookMutations</c> in
    /// <see cref="Tests.Services.Mutations.BookEditMutationTests"/>. What is left here is the one thing
    /// only the command layer decides: which of the mutations' expected outcomes is flattened back to
    /// <c>200 { "newEntityId": null }</c>. Every gesture below was a silent no-op before this
    /// migration — an unknown target simply found nothing, and an unchanged value saved nothing — and
    /// ADR 0007 keeps the endpoint's contract, so the outcomes the mutations now state out loud must
    /// still reach an agent as null.
    /// </para>
    /// </summary>
    public class BookEditCommandContractTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public BookEditCommandContractTests()
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

        private async Task<Guid?> RunAsync(BookCommand command)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBookCommandHandler>().ExecuteAsync(command);
        }

        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                    .AddPart("part", p => p
                        .AddChapter("ch", c => c
                            .AddParagraph("para", pg => pg.AddNarration("item", "Hello world")))))
                .BuildAsync();
            return b;
        }

        public static TheoryData<string> Gestures() => ["volume", "part", "chapter", "item", "program"];

        [Theory]
        [MemberData(nameof(Gestures))]
        public async Task AGestureAgainstSomethingTheBookDoesNotHave_AnswersNull(string gesture)
        {
            await SeedAsync();
            var missing = Guid.NewGuid();

            BookCommand command = gesture switch
            {
                "volume" => new UpdateVolumeTitleCommand(_folder, missing, "T"),
                "part" => new UpdatePartTitleCommand(_folder, missing, "T"),
                "chapter" => new UpdateChapterTitleCommand(_folder, missing, "T"),
                "item" => new UpdateParagraphItemTextCommand(_folder, missing, "T"),
                _ => new ApplyBookEditsCommand(_folder,
                    [new BookEditItem(BookEditTargetKind.ChapterTitle, missing, "T")]),
            };

            Assert.Null(await RunAsync(command));
        }

        [Theory]
        [MemberData(nameof(Gestures))]
        public async Task AGestureThatWritesTheValueAlreadyThere_AnswersNull(string gesture)
        {
            var b = await SeedAsync();
            await RunAsync(new UpdateVolumeTitleCommand(_folder, b.VolumeId("vol"), "V"));
            await RunAsync(new UpdatePartTitleCommand(_folder, b.PartId("part"), "P"));
            await RunAsync(new UpdateChapterTitleCommand(_folder, b.ChapterId("ch"), "C"));

            BookCommand command = gesture switch
            {
                "volume" => new UpdateVolumeTitleCommand(_folder, b.VolumeId("vol"), "V"),
                "part" => new UpdatePartTitleCommand(_folder, b.PartId("part"), "P"),
                "chapter" => new UpdateChapterTitleCommand(_folder, b.ChapterId("ch"), "C"),
                "item" => new UpdateParagraphItemTextCommand(_folder, b.ItemId("item"), "Hello world"),
                _ => new ApplyBookEditsCommand(_folder,
                    [new BookEditItem(BookEditTargetKind.ChapterTitle, b.ChapterId("ch"), "C")]),
            };

            Assert.Null(await RunAsync(command));
        }

        /// <summary>A successful edit creates nothing, so it answers null too — as it always has.</summary>
        [Fact]
        public async Task AnAppliedEditProgram_AnswersNull_AndIsWritten()
        {
            var b = await SeedAsync();

            var answer = await RunAsync(new ApplyBookEditsCommand(_folder,
            [
                new BookEditItem(BookEditTargetKind.ChapterTitle, b.ChapterId("ch"), "Chapter One"),
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Goodbye world"),
            ]));

            Assert.Null(answer);
            await using var verify = await OpenDbAsync();
            Assert.Equal("Chapter One", (await verify.Chapters.AsNoTracking().SingleAsync()).Title);
            Assert.Equal("Goodbye world", (await verify.ParagraphItems.AsNoTracking().SingleAsync()).Text);
        }
    }
}
