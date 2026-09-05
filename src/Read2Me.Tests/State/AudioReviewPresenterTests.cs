using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.App.State.Projection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.State
{
    /// <summary>
    /// The MudBlazor adapter's half of an audio review (ADR 0007): dismissing one commits a Book
    /// mutation, and the two singletons the tree renders reviews from — the review mirror behind the
    /// chip and the Node Status behind the badge — are reseeded from the snapshot that mutation
    /// produced, so they cannot contradict each other.
    /// <para>
    /// Everything below the adapter is real. What the write reports is asserted in
    /// <c>AudioRecordingMutationTests</c>; how a Book View reconciles it, in
    /// <c>BookViewProjectionTests</c>.
    /// </para>
    /// </summary>
    public class AudioReviewPresenterTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _circuit;
        private readonly ProjectFolderId _folder;
        private readonly BookHierarchyPresenter _presenter;
        private readonly AudioReviewService _reviews = new();
        private readonly NodeStatusService _nodeStatus = new(new FakeParagraphQueueProbe());

        public AudioReviewPresenterTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _circuit = _root.CreateAsyncScope();

            _folder = new ProjectFolderId(FolderName);
            var reader = _circuit.ServiceProvider.GetRequiredService<ProjectReader>();

            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var characterQueue = new CharacterQueueService();
            var snackbar = Substitute.For<ISnackbar>();
            var ttsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            ttsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);

            var coordinator = new BookSelectionCoordinator(
                reader, characterQueue, new AudioQueueService(), ttsSettings, snackbar,
                selectionState, audioSelectionState, new FakeAiPreflight());

            var projection = new BookViewProjection(
                new BookProjectLoader(reader), reader, reader, reader,
                _circuit.ServiceProvider.GetRequiredService<BookMutations>(),
                new BookTreeState(), selectionState, audioSelectionState, coordinator,
                new FakeVoiceResolver(),
                _root.GetRequiredService<BookRevisionSequence>(),
                _circuit.ServiceProvider.GetRequiredService<ProjectDbSession>(),
                _root.GetRequiredService<EventBroadcaster<BookMutationReceipt>>(),
                NullLogger<BookViewProjection>.Instance);

            _presenter = new BookHierarchyPresenter(
                reader, projection, _circuit.ServiceProvider.GetRequiredService<IBookCommandHandler>(),
                new FakeBookUseCases(), selectionState, audioSelectionState,
                Substitute.For<IDialogService>(), snackbar, characterQueue, _reviews, _nodeStatus);
        }

        public override async ValueTask DisposeAsync()
        {
            _presenter.Dispose();
            await _circuit.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private async Task<BookHierarchyBuilder> SeedWithAFailedTakeAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                    .AddParagraph("p", p => p.AddNarration("item", "Hello"))))
                .BuildAsync();

            await using var scope = _root.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(
                new RecordParagraphItemAudioMutation(_folder, b.ItemId("item"), "audio/item.wav",
                    new AudioReviewVerdict(
                        NormalizeOk: true, NormalizeReason: null,
                        VerifyOk: false, Wer: 0.42, VerifyReason: "over threshold",
                        Transcript: "heard", OriginalTextSnapshot: "Hello")));
            return b;
        }

        [Fact]
        public async Task LoadAsync_SeedsTheReviewChipAndTheReviewBadgeFromTheSnapshot()
        {
            var b = await SeedWithAFailedTakeAsync();

            await _presenter.LoadAsync(_folder);

            Assert.Equal(AudioReviewState.NeedsReview, _reviews.ReviewOf(_folder, b.ItemId("item"))!.State);
            Assert.Equal(1, _nodeStatus.StatusForNode(_folder, b.ChapterId("ch")).Review);
        }

        [Fact]
        public async Task DismissAudioReviewAsync_SilencesTheChipAndTheBadgeTogether()
        {
            var b = await SeedWithAFailedTakeAsync();
            await _presenter.LoadAsync(_folder);

            await _presenter.DismissAudioReviewAsync(_folder, b.ItemId("item"));

            Assert.Equal(AudioReviewState.Dismissed, _reviews.ReviewOf(_folder, b.ItemId("item"))!.State);
            Assert.Equal(0, _nodeStatus.StatusForNode(_folder, b.ChapterId("ch")).Review);
        }
    }
}
