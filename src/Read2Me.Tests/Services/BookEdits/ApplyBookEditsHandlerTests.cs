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

namespace Read2Me.Tests.Services.BookEdits
{
    public class ApplyBookEditsHandlerTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public ApplyBookEditsHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            _svc = sp.GetRequiredService<BookCommandHandler>();
            _folder = new ProjectFolderId(FolderName);
        }

        [Fact]
        public async Task ApplyBookEdits_UpdatesAllTargetKinds()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddPart("part", p => p
                    .AddChapter("ch", c => c
                        .AddParagraph(configure: pg => pg.AddNarration("item", "ello world")))))
                .BuildAsync();

            await _svc.ExecuteAsync(new ApplyBookEditsCommand(_folder, new[]
            {
                new BookEditItem(BookEditTargetKind.VolumeTitle, b.VolumeId("vol"), "Volume One"),
                new BookEditItem(BookEditTargetKind.PartTitle, b.PartId("part"), "Part One"),
                new BookEditItem(BookEditTargetKind.ChapterTitle, b.ChapterId("ch"), "Chapter One"),
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Hello world"),
            }));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Volume One", (await verify.Volumes.AsNoTracking().SingleAsync()).Title);
            Assert.Equal("Part One", (await verify.Parts.AsNoTracking().SingleAsync()).Title);
            Assert.Equal("Chapter One", (await verify.Chapters.AsNoTracking().SingleAsync()).Title);
            Assert.Equal("Hello world", (await verify.ParagraphItems.AsNoTracking().SingleAsync()).Text);
        }

        [Fact]
        public async Task ApplyBookEdits_SkipsMissingEntities_AppliesRest()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch"))
                .BuildAsync();

            await _svc.ExecuteAsync(new ApplyBookEditsCommand(_folder, new[]
            {
                new BookEditItem(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), "Ghost"),
                new BookEditItem(BookEditTargetKind.ChapterTitle, b.ChapterId("ch"), "Renamed"),
            }));

            await using var verify = await OpenDbAsync();
            var chapter = await verify.Chapters.AsNoTracking().SingleAsync(c => c.Id == b.ChapterId("ch"));
            Assert.Equal("Renamed", chapter.Title);
        }

        [Fact]
        public async Task ApplyBookEdits_EmptyList_Succeeds()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

            var result = await _svc.ExecuteAsync(new ApplyBookEditsCommand(_folder, Array.Empty<BookEditItem>()));
            Assert.Null(result);
        }

        /// <summary>
        /// This handler reaches item text by its own path rather than through
        /// <c>UpdateParagraphItemTextHandler</c>, so the stale-audio rule has to be asserted here in
        /// its own right — otherwise an AI edit would leave a WAV speaking the pre-edit words.
        /// </summary>
        [Fact]
        public async Task ApplyBookEdits_ItemTextChanged_ClearsAudioAndDeletesReview()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                    .AddParagraph(configure: pg => pg.AddNarration("item", "ello world"))))
                .BuildAsync();

            await using (var arrange = await OpenDbAsync())
            {
                (await arrange.ParagraphItems.FindAsync(b.ItemId("item")))!.AudioFileName = "item.wav";
                arrange.AudioReviews.Add(new AudioReview
                {
                    Id = Guid.NewGuid(),
                    ParagraphItemId = b.ItemId("item"),
                    State = Read2Me.Data.Enums.AudioReviewState.NeedsReview,
                    NormalizeOk = true,
                    VerifyOk = false,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });
                await arrange.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new ApplyBookEditsCommand(_folder, new[]
            {
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Hello world"),
            }));

            await using var verify = await OpenDbAsync();
            var item = await verify.ParagraphItems.AsNoTracking().SingleAsync();
            Assert.Equal("Hello world", item.Text);
            Assert.Null(item.AudioFileName);
            Assert.False(await verify.AudioReviews.AnyAsync());
        }

        /// <summary>An edit that rewrites nothing leaves good audio and its verdict alone.</summary>
        [Fact]
        public async Task ApplyBookEdits_ItemTextUnchanged_KeepsAudio()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                    .AddParagraph(configure: pg => pg.AddNarration("item", "Hello world"))))
                .BuildAsync();

            await using (var arrange = await OpenDbAsync())
            {
                (await arrange.ParagraphItems.FindAsync(b.ItemId("item")))!.AudioFileName = "item.wav";
                await arrange.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new ApplyBookEditsCommand(_folder, new[]
            {
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Hello world"),
            }));

            await using var verify = await OpenDbAsync();
            Assert.Equal("item.wav", (await verify.ParagraphItems.AsNoTracking().SingleAsync()).AudioFileName);
        }
    }
}
