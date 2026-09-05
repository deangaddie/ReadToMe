using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;
using EntityReviewState = Read2Me.Data.Enums.AudioReviewState;

namespace Read2Me.Tests.Services.Mutations
{
    /// <summary>
    /// The audio assignment and review family — the Audio Queue's recorded take, the reader's
    /// dismissal, and the two the generic command endpoint still posts — proved through
    /// <see cref="BookMutations.CommitAsync"/> against a real SQLite project.
    /// <para>
    /// Two things matter beyond the row-presence rules. The receipt must name the exact Paragraph
    /// and item, because this is the second family a Book View refreshes from instead of rebuilding;
    /// and recording a take must move the audio reference and the verdict on it in one commit, so
    /// that no reader can see a row playing new audio under the previous take's review chip.
    /// </para>
    /// </summary>
    public class AudioRecordingMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public AudioRecordingMutationTests()
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

        private async Task<BookMutationReceipt> CommittedAsync(BookMutation mutation) =>
            Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(mutation)).Receipt;

        private async Task<Guid?> ExecuteLegacyAsync(BookCommand command)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BookCommandHandler>().ExecuteAsync(command);
        }

        private BookHierarchyBuilder _book = null!;

        private async Task<Guid> SeedItemAsync()
        {
            _book = new BookHierarchyBuilder(OpenDbAsync);
            await _book.AddVolume("vol", v => v.AddChapter(configure: c => c
                    .AddParagraph("para", p => p.AddNarration("item", "Hello"))))
                .BuildAsync();
            return _book.ItemId("item");
        }

        private static AudioReviewVerdict Failure(double wer = 0.42) => new(
            NormalizeOk: true, NormalizeReason: null,
            VerifyOk: false, Wer: wer, VerifyReason: "over threshold",
            Transcript: "got this", OriginalTextSnapshot: "Hello");

        private static AudioReviewVerdict Clean() => new(
            NormalizeOk: true, NormalizeReason: null,
            VerifyOk: true, Wer: 0.01, VerifyReason: null,
            Transcript: "Hello", OriginalTextSnapshot: "Hello");

        private async Task<AudioReview?> ReviewAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return await db.AudioReviews.AsNoTracking().FirstOrDefaultAsync(r => r.ParagraphItemId == itemId);
        }

        private async Task<string?> AudioOfAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return (await db.ParagraphItems.AsNoTracking().SingleAsync(i => i.Id == itemId)).AudioFileName;
        }

        // ── recording a take ─────────────────────────────────────────────────

        [Fact]
        public async Task Recording_StampsTheAudioAndItsVerdictTogether()
        {
            var itemId = await SeedItemAsync();

            await CommitAsync(new RecordParagraphItemAudioMutation(
                _folder, itemId, "audio/one.wav", Failure()));

            Assert.Equal("audio/one.wav", await AudioOfAsync(itemId));
            var review = await ReviewAsync(itemId);
            Assert.Equal(EntityReviewState.NeedsReview, review!.State);
            Assert.Equal(0.42, review.Wer);
        }

        [Fact]
        public async Task Recording_NamesTheExactParagraphAndItemThatMoved()
        {
            var itemId = await SeedItemAsync();

            var receipt = await CommittedAsync(new RecordParagraphItemAudioMutation(
                _folder, itemId, "audio/one.wav", Failure()));

            Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
            Assert.Equal(BookFacets.Audio | BookFacets.Reviews, receipt.Effects.Facets);
            Assert.Equal([_book.ParagraphId("para")], receipt.Effects.ParagraphIds);
            Assert.Equal([itemId], receipt.Effects.ParagraphItemIds);
        }

        /// <summary>
        /// A clean take reports audio and nothing else: there was no review to remove, so a reader
        /// that reconciles reviews has nothing to do about this commit.
        /// </summary>
        [Fact]
        public async Task ACleanTakeWithNoPriorReview_ReportsAudioOnly()
        {
            var itemId = await SeedItemAsync();

            var receipt = await CommittedAsync(new RecordParagraphItemAudioMutation(
                _folder, itemId, "audio/one.wav", Clean()));

            Assert.Equal(BookFacets.Audio, receipt.Effects.Facets);
            Assert.Null(await ReviewAsync(itemId));
        }

        [Fact]
        public async Task ACleanTake_RemovesThePreviousTakesReview()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new RecordParagraphItemAudioMutation(_folder, itemId, "audio/one.wav", Failure()));

            var receipt = await CommittedAsync(new RecordParagraphItemAudioMutation(
                _folder, itemId, "audio/one.wav", Clean()));

            Assert.Equal(BookFacets.Audio | BookFacets.Reviews, receipt.Effects.Facets);
            Assert.Null(await ReviewAsync(itemId));
        }

        /// <summary>
        /// The file behind the path is a different take even when every column stays as it was, so
        /// recording is never a no-change: a reader has audio to reread and a cache to bust.
        /// </summary>
        [Fact]
        public async Task ARetakeThatMovesNoColumn_StillCommits()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new RecordParagraphItemAudioMutation(_folder, itemId, "audio/one.wav", Clean()));

            var receipt = await CommittedAsync(new RecordParagraphItemAudioMutation(
                _folder, itemId, "audio/one.wav", Clean()));

            Assert.Equal(BookFacets.Audio, receipt.Effects.Facets);
        }

        [Fact]
        public async Task Recording_AgainstAnItemTheBookNoLongerHas_IsRejected()
        {
            await SeedItemAsync();

            var outcome = await CommitAsync(new RecordParagraphItemAudioMutation(
                _folder, Guid.NewGuid(), "audio/ghost.wav", Clean()));

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
            Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);
        }

        // ── verdicts on audio already recorded ───────────────────────────────

        [Fact]
        public async Task AFailedStage_Twice_UpdatesTheOneRow()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Failure()));

            await CommitAsync(new SetAudioReviewMutation(_folder, itemId, new AudioReviewVerdict(
                NormalizeOk: false, NormalizeReason: "clipping",
                VerifyOk: false, Wer: 0.5, VerifyReason: "worse",
                Transcript: "x", OriginalTextSnapshot: "Hello")));

            await using var db = await OpenDbAsync();
            var rows = await db.AudioReviews.AsNoTracking().Where(r => r.ParagraphItemId == itemId).ToListAsync();
            Assert.Single(rows);
            Assert.Equal("clipping", rows[0].NormalizeReason);
            Assert.Equal(0.5, rows[0].Wer);
        }

        /// <summary>
        /// The legacy handler restamped <c>UpdatedUtc</c> whatever the verdict said, so re-recording
        /// an unchanged failure was a write every open Book View had to reconcile.
        /// </summary>
        [Fact]
        public async Task AVerdictIdenticalToTheOneAlreadyRecorded_ChangesNothing()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Failure()));

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Failure())));
        }

        [Fact]
        public async Task APassingVerdictOnAnItemWithNoReview_ChangesNothing()
        {
            var itemId = await SeedItemAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Clean())));
        }

        [Fact]
        public async Task AFreshFailure_ReturnsADismissedReviewToNeedsReview()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Failure()));
            await CommitAsync(new DismissAudioReviewMutation(_folder, itemId));

            await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Failure()));

            Assert.Equal(EntityReviewState.NeedsReview, (await ReviewAsync(itemId))!.State);
        }

        // ── dismissal ────────────────────────────────────────────────────────

        [Fact]
        public async Task Dismissing_SilencesTheReviewAndNamesTheItemItSilenced()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Failure()));

            var receipt = await CommittedAsync(new DismissAudioReviewMutation(_folder, itemId));

            Assert.Equal(EntityReviewState.Dismissed, (await ReviewAsync(itemId))!.State);
            Assert.Equal(BookFacets.Reviews, receipt.Effects.Facets);
            Assert.Equal([_book.ParagraphId("para")], receipt.Effects.ParagraphIds);
            Assert.Equal([itemId], receipt.Effects.ParagraphItemIds);
        }

        [Fact]
        public async Task DismissingAnItemWithNothingToDismiss_ChangesNothing()
        {
            var itemId = await SeedItemAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new DismissAudioReviewMutation(_folder, itemId)));
        }

        [Fact]
        public async Task DismissingTwice_ChangesNothingTheSecondTime()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new SetAudioReviewMutation(_folder, itemId, Failure()));
            await CommitAsync(new DismissAudioReviewMutation(_folder, itemId));

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new DismissAudioReviewMutation(_folder, itemId)));
        }

        // ── pointing an item at audio, with no verdict ───────────────────────

        [Fact]
        public async Task SettingTheAudioReference_ReportsAudioAlone()
        {
            var itemId = await SeedItemAsync();

            var receipt = await CommittedAsync(new SetParagraphItemAudioMutation(_folder, itemId, "audio/abc.wav"));

            Assert.Equal("audio/abc.wav", await AudioOfAsync(itemId));
            Assert.Equal(BookFacets.Audio, receipt.Effects.Facets);
        }

        [Fact]
        public async Task SettingTheAudioReferenceItAlreadyCarries_ChangesNothing()
        {
            var itemId = await SeedItemAsync();
            await CommitAsync(new SetParagraphItemAudioMutation(_folder, itemId, "audio/abc.wav"));

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetParagraphItemAudioMutation(_folder, itemId, "audio/abc.wav")));
        }

        // ── the generic command endpoint's contract, unchanged ───────────────

        /// <summary>
        /// <c>POST /api/projects/{folder}/commands</c> keeps the responses it had when these handlers
        /// owned the save: null for a change, for nothing to do, and for an item the Book does not
        /// contain alike.
        /// </summary>
        [Fact]
        public async Task TheCommandEndpointsThreeAudioCommands_StillAnswerNull()
        {
            var itemId = await SeedItemAsync();

            Assert.Null(await ExecuteLegacyAsync(new SetParagraphItemAudioCommand(_folder, itemId, "audio/abc.wav")));
            Assert.Null(await ExecuteLegacyAsync(new SetParagraphItemAudioCommand(_folder, Guid.NewGuid(), "audio/x.wav")));
            Assert.Null(await ExecuteLegacyAsync(new SetAudioReviewCommand(
                _folder, itemId,
                NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.42, VerifyReason: "over threshold",
                Transcript: "got this", OriginalTextSnapshot: "Hello")));
            Assert.Null(await ExecuteLegacyAsync(new DismissAudioReviewCommand(_folder, itemId)));
            Assert.Null(await ExecuteLegacyAsync(new DismissAudioReviewCommand(_folder, itemId)));

            Assert.Equal("audio/abc.wav", await AudioOfAsync(itemId));
            Assert.Equal(EntityReviewState.Dismissed, (await ReviewAsync(itemId))!.State);
        }
    }
}
