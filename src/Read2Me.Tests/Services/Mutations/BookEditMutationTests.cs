using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;
using EntityReviewState = Read2Me.Data.Enums.AudioReviewState;

namespace Read2Me.Tests.Services.Mutations
{
    /// <summary>
    /// The manual and AI edit family proved through <see cref="BookMutations.CommitAsync"/> against a
    /// real SQLite project (ADR 0007).
    /// <para>
    /// Three things matter beyond "the value was written". A rewritten item must lose its audio and
    /// the verdict on that audio, whichever half of the family did the rewriting — the AI route
    /// reached item text by its own path before this and left the hole open. The receipt must name
    /// what actually moved, because that is what decides whether an open Book View rereads the
    /// Paragraphs it was told about or rebuilds: a program that only rewrote item text is refreshable,
    /// one that moved a title is not. And a value that already says what was asked for must be
    /// <see cref="BookMutationOutcome.NoChange"/>, so re-confirming an edit costs no revision, no
    /// reconciliation and no lost audio.
    /// </para>
    /// </summary>
    public class BookEditMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public BookEditMutationTests()
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

        private async Task<BookMutationRejection> RefusedAsync(BookMutation mutation) =>
            Assert.IsType<BookMutationOutcome.Rejected>(await CommitAsync(mutation)).Reason;

        /// <summary>One volume, one part, one chapter, one paragraph, one narration item.</summary>
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

        /// <summary>Arranges a generated, reviewed item: it has a WAV and a NeedsReview verdict.</summary>
        private async Task GiveItemAudioAndReviewAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            (await db.ParagraphItems.FindAsync(itemId))!.AudioFileName = "item.wav";
            db.AudioReviews.Add(new AudioReview
            {
                Id = Guid.NewGuid(),
                ParagraphItemId = itemId,
                State = EntityReviewState.NeedsReview,
                NormalizeOk = true,
                VerifyOk = false,
                Wer = 0.4,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // ── node titles ──────────────────────────────────────────────────────

        [Fact]
        public async Task RetitlingAVolume_WritesTheTitle_AndNamesTheVolume()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new UpdateVolumeTitleMutation(_folder, b.VolumeId("vol"), "Volume One"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Volume One", (await verify.Volumes.AsNoTracking().SingleAsync()).Title);
            Assert.Equal(BookMutationScope.Exact, effects.Scope);
            Assert.Equal(BookFacets.NodeTitle, effects.Facets);
            Assert.Equal([b.VolumeId("vol")], effects.NodeIds);
            Assert.Empty(effects.ParagraphIds);
        }

        [Fact]
        public async Task RetitlingAPart_WritesTheTitle_AndNamesThePart()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new UpdatePartTitleMutation(_folder, b.PartId("part"), "Part One"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Part One", (await verify.Parts.AsNoTracking().SingleAsync()).Title);
            Assert.Equal([b.PartId("part")], effects.NodeIds);
        }

        [Fact]
        public async Task RetitlingAChapter_WritesTheTitle_AndNamesTheChapter()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new UpdateChapterTitleMutation(_folder, b.ChapterId("ch"), "Chapter One"));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Chapter One", (await verify.Chapters.AsNoTracking().SingleAsync()).Title);
            Assert.Equal([b.ChapterId("ch")], effects.NodeIds);
        }

        /// <summary>
        /// A title is reported as its own facet rather than as structure. Nothing was created, moved
        /// or deleted, so no reader has to drop a selection or an expanded branch over it — but it is
        /// also not on a Paragraph, which is why it never claims to be row-refreshable.
        /// </summary>
        [Fact]
        public async Task ARetitle_IsNotStructural()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new UpdateChapterTitleMutation(_folder, b.ChapterId("ch"), "Chapter One"));

            Assert.False(effects.Facets.HasFlag(BookFacets.Structure));
            Assert.Empty(effects.Structural);
        }

        [Fact]
        public async Task RetitlingToTheTitleAlreadyThere_ChangesNothing()
        {
            var b = await SeedAsync();
            await CommitAsync(new UpdateChapterTitleMutation(_folder, b.ChapterId("ch"), "Chapter One"));

            var outcome = await CommitAsync(new UpdateChapterTitleMutation(_folder, b.ChapterId("ch"), "Chapter One"));

            Assert.IsType<BookMutationOutcome.NoChange>(outcome);
        }

        [Theory]
        [InlineData("volume")]
        [InlineData("part")]
        [InlineData("chapter")]
        [InlineData("item")]
        public async Task EditingSomethingTheBookDoesNotHave_IsRefusedAsNotFound(string target)
        {
            await SeedAsync();
            var missing = Guid.NewGuid();

            BookMutation mutation = target switch
            {
                "volume" => new UpdateVolumeTitleMutation(_folder, missing, "T"),
                "part" => new UpdatePartTitleMutation(_folder, missing, "T"),
                "chapter" => new UpdateChapterTitleMutation(_folder, missing, "T"),
                _ => new UpdateParagraphItemTextMutation(_folder, missing, "T"),
            };

            Assert.Equal(BookMutationRejection.NotFound, await RefusedAsync(mutation));
        }

        // ── item text ────────────────────────────────────────────────────────

        /// <summary>
        /// The rewritten item's WAV speaks words it no longer has, and while it still <em>has</em>
        /// audio it is not Generatable — a "select needs audio" pass would skip it and the mismatch
        /// would assemble into the exported m4b. The verdict goes with the audio it judged.
        /// </summary>
        [Fact]
        public async Task RewritingAnItem_DiscardsItsAudioAndTheVerdictOnIt()
        {
            var b = await SeedAsync();
            await GiveItemAudioAndReviewAsync(b.ItemId("item"));

            var effects = await AppliedAsync(
                new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Goodbye world"));

            await using var verify = await OpenDbAsync();
            var item = await verify.ParagraphItems.AsNoTracking().SingleAsync();
            Assert.Equal("Goodbye world", item.Text);
            Assert.Null(item.AudioFileName);
            Assert.False(await verify.AudioReviews.AnyAsync());

            Assert.Equal(BookMutationScope.Exact, effects.Scope);
            Assert.Equal(BookFacets.ItemText | BookFacets.Audio | BookFacets.Reviews, effects.Facets);
            Assert.Equal([b.ParagraphId("para")], effects.ParagraphIds);
            Assert.Equal([b.ItemId("item")], effects.ParagraphItemIds);
        }

        /// <summary>
        /// The facets say what this write actually did. An item that had no audio and no verdict
        /// gives a reader no audio state to reread, and claiming otherwise would have every open Book
        /// View rechecking eligibility for nothing.
        /// </summary>
        [Fact]
        public async Task RewritingAnItemWithNoAudio_ReportsOnlyTheText()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(
                new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Goodbye world"));

            Assert.Equal(BookFacets.ItemText, effects.Facets);
        }

        /// <summary>
        /// Saving without editing anything must not cost a regeneration — and the review row is a
        /// verdict on audio that is still current, so it stays too.
        /// </summary>
        [Fact]
        public async Task RewritingAnItemToTheTextItAlreadyHas_ChangesNothing()
        {
            var b = await SeedAsync();
            await GiveItemAudioAndReviewAsync(b.ItemId("item"));

            var outcome = await CommitAsync(
                new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Hello world"));

            Assert.IsType<BookMutationOutcome.NoChange>(outcome);
            await using var verify = await OpenDbAsync();
            Assert.Equal("item.wav", (await verify.ParagraphItems.AsNoTracking().SingleAsync()).AudioFileName);
            Assert.True(await verify.AudioReviews.AnyAsync());
        }

        // ── AI edit programs ─────────────────────────────────────────────────

        [Fact]
        public async Task AnEditProgram_AppliesEveryTargetKindInOneCommit()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new ApplyBookEditsMutation(_folder,
            [
                new BookEditItem(BookEditTargetKind.VolumeTitle, b.VolumeId("vol"), "Volume One"),
                new BookEditItem(BookEditTargetKind.PartTitle, b.PartId("part"), "Part One"),
                new BookEditItem(BookEditTargetKind.ChapterTitle, b.ChapterId("ch"), "Chapter One"),
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Goodbye world"),
            ]));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Volume One", (await verify.Volumes.AsNoTracking().SingleAsync()).Title);
            Assert.Equal("Part One", (await verify.Parts.AsNoTracking().SingleAsync()).Title);
            Assert.Equal("Chapter One", (await verify.Chapters.AsNoTracking().SingleAsync()).Title);
            Assert.Equal("Goodbye world", (await verify.ParagraphItems.AsNoTracking().SingleAsync()).Text);

            Assert.Equal(BookFacets.NodeTitle | BookFacets.ItemText, effects.Facets);
            Assert.Equal(
                [b.VolumeId("vol"), b.PartId("part"), b.ChapterId("ch")],
                effects.NodeIds);
            Assert.Equal([b.ParagraphId("para")], effects.ParagraphIds);
            Assert.Equal([b.ItemId("item")], effects.ParagraphItemIds);
        }

        /// <summary>
        /// The reason a program that only rewrote text is worth telling apart: it names Paragraphs and
        /// no facet that lives off one, which is the whole condition an open Book View refreshes rows
        /// on instead of rereading its expanded branches.
        /// </summary>
        [Fact]
        public async Task AnEditProgramOfOnlyItemText_NamesParagraphsAndNoTitleFacet()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new ApplyBookEditsMutation(_folder,
            [
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Goodbye world"),
            ]));

            Assert.Equal(BookMutationScope.Exact, effects.Scope);
            Assert.False(effects.Facets.HasFlag(BookFacets.NodeTitle));
            Assert.Equal([b.ParagraphId("para")], effects.ParagraphIds);
            Assert.Empty(effects.NodeIds);
        }

        /// <summary>An AI rewrite is as explicit as a hand one, so it costs the same stale audio.</summary>
        [Fact]
        public async Task AnEditProgram_DiscardsTheAudioOfEveryItemItRewrites()
        {
            var b = await SeedAsync();
            await GiveItemAudioAndReviewAsync(b.ItemId("item"));

            var effects = await AppliedAsync(new ApplyBookEditsMutation(_folder,
            [
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Goodbye world"),
            ]));

            await using var verify = await OpenDbAsync();
            Assert.Null((await verify.ParagraphItems.AsNoTracking().SingleAsync()).AudioFileName);
            Assert.False(await verify.AudioReviews.AnyAsync());
            Assert.Equal(BookFacets.ItemText | BookFacets.Audio | BookFacets.Reviews, effects.Facets);
        }

        /// <summary>
        /// The program was planned against a Book the producer has been reviewing, possibly while
        /// another circuit edited it. One vanished target is not a reason to throw away the rows they
        /// approved.
        /// </summary>
        [Fact]
        public async Task AnEditProgram_SkipsWhatTheBookNoLongerHas_AndAppliesTheRest()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new ApplyBookEditsMutation(_folder,
            [
                new BookEditItem(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), "Ghost"),
                new BookEditItem(BookEditTargetKind.ChapterTitle, b.ChapterId("ch"), "Renamed"),
            ]));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Renamed", (await verify.Chapters.AsNoTracking().SingleAsync()).Title);
            Assert.Equal([b.ChapterId("ch")], effects.NodeIds);
        }

        /// <summary>
        /// A reader told to reread a Paragraph twice rereads it twice, so a program holding two rows
        /// against the same target names it once however many times it touched it.
        /// </summary>
        [Fact]
        public async Task AnEditProgramWithTwoRowsAgainstOneItem_NamesItOnce()
        {
            var b = await SeedAsync();

            var effects = await AppliedAsync(new ApplyBookEditsMutation(_folder,
            [
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "First pass"),
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Second pass"),
            ]));

            await using var verify = await OpenDbAsync();
            Assert.Equal("Second pass", (await verify.ParagraphItems.AsNoTracking().SingleAsync()).Text);
            Assert.Equal([b.ItemId("item")], effects.ParagraphItemIds);
            Assert.Equal([b.ParagraphId("para")], effects.ParagraphIds);
        }

        [Fact]
        public async Task AnEmptyEditProgram_ChangesNothing()
        {
            await SeedAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new ApplyBookEditsMutation(_folder, [])));
        }

        /// <summary>
        /// Every row proposing the wording the Book already has — a reviewer who unticked the real
        /// changes, or a second Apply of a program already applied — costs no revision and no
        /// reconciliation in any open Book View.
        /// </summary>
        [Fact]
        public async Task AnEditProgramThatProposesWhatIsAlreadyThere_ChangesNothing()
        {
            var b = await SeedAsync();

            var outcome = await CommitAsync(new ApplyBookEditsMutation(_folder,
            [
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("item"), "Hello world"),
                new BookEditItem(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), "Ghost"),
            ]));

            Assert.IsType<BookMutationOutcome.NoChange>(outcome);
        }
    }
}
