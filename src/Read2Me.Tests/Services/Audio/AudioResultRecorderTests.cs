using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;
using Read2Me.Services.Queueing;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// The Audio Queue's write adapter (ADR 0007). What is under test is the ordering the persisted
    /// Book depends on: the take is staged beside its destination, the mutation commits, and only
    /// then does the WAV take the name the Book now carries.
    /// <para>
    /// The write side is real — a SQLite project and <see cref="BookMutations"/> — because "the item
    /// points at the file that is actually there" is a claim about both at once. What the mutation
    /// reports is asserted in <c>AudioRecordingMutationTests</c>.
    /// </para>
    /// </summary>
    public class AudioResultRecorderTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly FakeFileSystem _fs;
        private readonly ProjectFolderId _folder;

        public AudioResultRecorderTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();

            _folder = new ProjectFolderId(FolderName);
            _fs = new FakeFileSystem(TempDir);
            _fs.SeedFolder(FolderName);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private AudioResultRecorder Sut()
        {
            var scope = _root.CreateScope();
            return new AudioResultRecorder(
                _fs, scope.ServiceProvider.GetRequiredService<BookMutations>(),
                NullLogger<AudioResultRecorder>.Instance);
        }

        private async Task<Guid> SeedItemAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                    .AddParagraph(configure: p => p.AddNarration("item", "In a hole in the ground"))))
                .BuildAsync();
            return b.ItemId("item");
        }

        private static PipelineResult Clean(byte[]? audio = null) => new(
            AudioBytes: audio ?? [0x52, 0x49, 0x46, 0x46],
            Normalize: new NormalizeOutcome(Ok: true, Reason: null),
            Verify: new VerifyOutcome(Ok: true, Wer: 0.0, Reason: null, Transcript: "In a hole in the ground", Rescued: false),
            Outcome: new WorkOutcome.Ok());

        private static PipelineResult FailedVerify() => new(
            AudioBytes: [0x52, 0x49, 0x46, 0x46],
            Normalize: new NormalizeOutcome(Ok: true, Reason: null),
            Verify: new VerifyOutcome(Ok: false, Wer: 0.42, Reason: "WER 0.42", Transcript: "wrong", Rescued: false),
            Outcome: new WorkOutcome.Ok());

        private string PathOf(Guid itemId) =>
            Path.Combine(_fs.GetProjectFolderPath(FolderName), "audio", $"{itemId}.wav");

        private async Task<ParagraphItem> ItemAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return await db.ParagraphItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        }

        private async Task<AudioReview?> ReviewAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return await db.AudioReviews.AsNoTracking().FirstOrDefaultAsync(r => r.ParagraphItemId == itemId);
        }

        [Fact]
        public async Task Records_TheAudioReference_AndLeavesTheWavWhereTheBookSaysItIs()
        {
            var itemId = await SeedItemAsync();

            var relativePath = await Sut().RecordAsync(_folder, itemId, Clean(), "In a hole in the ground", TestContext.Current.CancellationToken);

            Assert.Equal($"audio/{itemId}.wav", relativePath);
            Assert.Equal(relativePath, (await ItemAsync(itemId)).AudioFileName);
            Assert.True(_fs.FileExists(PathOf(itemId)));
            // Nothing left staged: the promotion moved the file rather than copying it.
            Assert.DoesNotContain(_fs.GetAllPaths(), p => p.EndsWith(".staging", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AFailedStage_RecordsTheVerdictWithTheAudioItJudged()
        {
            var itemId = await SeedItemAsync();

            await Sut().RecordAsync(_folder, itemId, FailedVerify(), "In a hole in the ground", TestContext.Current.CancellationToken);

            var review = await ReviewAsync(itemId);
            Assert.NotNull(review);
            Assert.False(review!.VerifyOk);
            Assert.Equal(0.42, review.Wer);
            Assert.Equal($"audio/{itemId}.wav", (await ItemAsync(itemId)).AudioFileName);
        }

        [Fact]
        public async Task ACleanTake_RemovesThePreviousTakesReview()
        {
            var itemId = await SeedItemAsync();
            await Sut().RecordAsync(_folder, itemId, FailedVerify(), "In a hole in the ground", TestContext.Current.CancellationToken);

            await Sut().RecordAsync(_folder, itemId, Clean(), "In a hole in the ground", TestContext.Current.CancellationToken);

            Assert.Null(await ReviewAsync(itemId));
        }

        /// <summary>
        /// A re-record of an item whose columns do not move — same path, same clean verdict — is
        /// still a new take, so the file the Book names is the one just generated.
        /// </summary>
        [Fact]
        public async Task ARetakeThatMovesNoColumn_StillReplacesTheAudioOnDisk()
        {
            var itemId = await SeedItemAsync();
            await Sut().RecordAsync(_folder, itemId, Clean(), "In a hole in the ground", TestContext.Current.CancellationToken);

            byte[] retake = [0x52, 0x49, 0x46, 0x46, 0x99];
            await Sut().RecordAsync(_folder, itemId, Clean(retake), "In a hole in the ground", TestContext.Current.CancellationToken);

            Assert.Equal(retake, _fs.GetFileContent(PathOf(itemId)));
        }

        /// <summary>
        /// The item was deleted while its take was generating. The Book cannot name the WAV, so the
        /// staged file goes with the rejection rather than being left behind for nothing to claim.
        /// </summary>
        [Fact]
        public async Task AnUncommittedRecording_LeavesNoAudioBehind()
        {
            await SeedItemAsync();
            var ghost = Guid.NewGuid();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Sut().RecordAsync(_folder, ghost, Clean(), "gone", TestContext.Current.CancellationToken));

            Assert.Empty(_fs.GetAllPaths());
        }

        /// <summary>
        /// The audio the item already had survives a recording that does not commit: the take is
        /// staged elsewhere, so a rejection cannot destroy the take before it.
        /// </summary>
        [Fact]
        public async Task AnUncommittedRecording_LeavesTheItemsExistingAudioAlone()
        {
            var itemId = await SeedItemAsync();
            await Sut().RecordAsync(_folder, itemId, Clean(), "In a hole in the ground", TestContext.Current.CancellationToken);
            var recorded = _fs.GetFileContent(PathOf(itemId));

            await using (var db = await OpenDbAsync())
            {
                db.ParagraphItems.RemoveRange(db.ParagraphItems.Where(i => i.Id == itemId));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Sut().RecordAsync(_folder, itemId, Clean([0x52, 0x49, 0x46, 0x46, 0x01]), "gone", TestContext.Current.CancellationToken));

            Assert.Equal(recorded, _fs.GetFileContent(PathOf(itemId)));
        }
    }
}
