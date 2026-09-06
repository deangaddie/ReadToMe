using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.App.State.Projection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Queueing;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.State
{
    /// <summary>
    /// The MudBlazor adapter's half of stale recovery (ADR 0007): what the page can show and offer
    /// once its <see cref="BookViewProjection"/> has given up on reconciling, and what the reader is
    /// told about a change the Book kept but the Book View could not display.
    /// <para>
    /// Everything below the adapter is real except the one read the tests have to break — a Book
    /// View only goes stale when an authoritative read fails, and no assertion about the banner
    /// means anything if the staleness behind it was faked.
    /// </para>
    /// </summary>
    public class BookViewRecoveryPresenterTests : ProjectDbTestBase
    {
        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();

        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _circuit;
        private readonly ProjectFolderId _folder;
        private readonly ProjectReader _reader;
        private readonly SwitchableLoader _loader;
        private readonly BookHierarchyPresenter _presenter;
        private readonly ISnackbar _snackbar = Substitute.For<ISnackbar>();

        public BookViewRecoveryPresenterTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _circuit = _root.CreateAsyncScope();

            _folder = new ProjectFolderId(FolderName);
            _reader = _circuit.ServiceProvider.GetRequiredService<ProjectReader>();
            _loader = new SwitchableLoader(new BookProjectLoader(_reader));

            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var ttsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            ttsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);

            var coordinator = new BookSelectionCoordinator(
                _reader, new CharacterQueueService(), new AudioQueueService(), ttsSettings, _snackbar,
                selectionState, audioSelectionState, new FakeAiPreflight());

            var projection = new BookViewProjection(
                _loader, _reader, _reader, _reader,
                _circuit.ServiceProvider.GetRequiredService<BookMutations>(),
                new BookTreeState(), selectionState, audioSelectionState, coordinator,
                new FakeVoiceResolver(),
                _root.GetRequiredService<BookRevisionSequence>(),
                _circuit.ServiceProvider.GetRequiredService<ProjectDbSession>(),
                _root.GetRequiredService<EventBroadcaster<BookMutationReceipt>>(),
                NullLogger<BookViewProjection>.Instance);

            _presenter = new BookHierarchyPresenter(
                _reader, projection, _circuit.ServiceProvider.GetRequiredService<CharacterResolver>(),
                new FakeBookUseCases(), selectionState, audioSelectionState,
                Substitute.For<IDialogService>(), _snackbar,
                new CharacterQueueService(), new AudioReviewService(),
                new NodeStatusService(new FakeParagraphQueueProbe()));
        }

        // ── arrangement ──────────────────────────────────────────────────────

        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .WithCharacter("bob", new Character { Id = BobId, Name = "Bob" });
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                    .AddParagraph("p1", p => p
                        .AddCharacterLine("d1", "\"One.\" ", "alice")
                        .AddNarration("n1", "she said."))
                    .AddParagraph("p2", p => p
                        .AddCharacterLine("d2", "\"Two.\" ", "alice")
                        .AddNarration("n2", "she added."))))
                .BuildAsync();
            return b;
        }

        /// <summary>Opens the Book View with the chapter expanded, so its Paragraphs are loaded.</summary>
        private async Task OpenAsync(BookHierarchyBuilder b)
        {
            await _presenter.LoadAsync(_folder);
            await _presenter.SetNodeExpandedAsync(BookNodeLevel.Chapter, b.ChapterId("ch"), true);
        }

        /// <summary>
        /// Commits one real speaker change with every authoritative read broken, so both
        /// reconciliation attempts fail and the Book View is left stale — the state under test.
        /// </summary>
        private async Task GoStaleAsync(BookHierarchyBuilder b)
        {
            _loader.Failure = new InvalidOperationException("the database went away");
            await _presenter.SetItemCharacterAsync(_folder, Line(b, "d1"), BobId);
        }

        private ParagraphItem Line(BookHierarchyBuilder b, string item) =>
            _presenter.Paragraphs(b.ChapterId("ch"))!
                .SelectMany(p => p.Items)
                .Single(i => i.Id == b.ItemId(item));

        // ── the stale indicator ──────────────────────────────────────────────

        [Fact]
        public async Task AFailedReconciliation_RaisesTheStaleIndicatorAndRepaints()
        {
            var b = await SeedAsync();
            await OpenAsync(b);
            var repaints = 0;
            _presenter.StateChanged += () => repaints++;

            await GoStaleAsync(b);

            Assert.True(_presenter.IsStale);
            Assert.False(_presenter.CanMutate);
            // The banner only appears if something told the page to render again.
            Assert.True(repaints > 0, "the Book View was never repainted after going stale");
        }

        [Fact]
        public async Task AStaleBookView_StillRendersTheLastCoherentContent()
        {
            var b = await SeedAsync();
            await OpenAsync(b);

            await GoStaleAsync(b);

            Assert.True(_presenter.HasContent);
            Assert.Equal(2, _presenter.Paragraphs(b.ChapterId("ch"))!.Count);
            Assert.Contains(_presenter.Characters, c => c.Id == BobId);
        }

        // ── committed-but-stale messaging ────────────────────────────────────

        /// <summary>
        /// The one thing the reader must not conclude is that their change was lost, because the
        /// gesture that saved it is the same one they would repeat.
        /// </summary>
        [Fact]
        public async Task ACommittedButStaleGesture_SaysTheChangeWasSaved()
        {
            var b = await SeedAsync();
            await OpenAsync(b);

            await GoStaleAsync(b);

            Assert.Equal(BobId, await PersistedSpeakerOfAsync(b.ItemId("d1")));
            _snackbar.Received(1).Add(
                Arg.Is<string>(m => m != null && m.Contains("saved") && m.Contains("do not repeat")),
                Severity.Warning, Arg.Any<Action<SnackbarOptions>>(), Arg.Any<string>());
        }

        // ── blocked mutations ────────────────────────────────────────────────

        [Fact]
        public async Task AMutationGestureWhileStale_IsRefusedAndSaysWhy()
        {
            var b = await SeedAsync();
            await OpenAsync(b);
            await GoStaleAsync(b);
            _snackbar.ClearReceivedCalls();
            _loader.Failure = null;

            await _presenter.SetItemCharacterAsync(_folder, Line(b, "d2"), BobId);

            Assert.NotEqual(BobId, await PersistedSpeakerOfAsync(b.ItemId("d2")));
            _snackbar.Received(1).Add(
                Arg.Is<string>(m => m != null && m.Contains("out of date")),
                Severity.Warning, Arg.Any<Action<SnackbarOptions>>(), Arg.Any<string>());
        }

        // ── the retry action ─────────────────────────────────────────────────

        [Fact]
        public async Task Retry_OnceTheReadRecovers_ClearsTheBannerAndReopensTheBookToChanges()
        {
            var b = await SeedAsync();
            await OpenAsync(b);
            await GoStaleAsync(b);
            _loader.Failure = null;

            await _presenter.RetryRebuildAsync();

            Assert.False(_presenter.IsStale);
            Assert.True(_presenter.CanMutate);
            // The change the failed reconciliation could not show is on screen now.
            Assert.Equal(BobId, Line(b, "d1").CharacterId);

            await _presenter.SetItemCharacterAsync(_folder, Line(b, "d2"), BobId);
            Assert.Equal(BobId, await PersistedSpeakerOfAsync(b.ItemId("d2")));
        }

        [Fact]
        public async Task Retry_ThatFailsAgain_LeavesTheBannerUpAndSaysSo()
        {
            var b = await SeedAsync();
            await OpenAsync(b);
            await GoStaleAsync(b);
            _snackbar.ClearReceivedCalls();

            await _presenter.RetryRebuildAsync();

            Assert.True(_presenter.IsStale);
            _snackbar.Received(1).Add(
                Arg.Is<string>(m => m != null && m.Contains("still could not be refreshed")),
                Severity.Warning, Arg.Any<Action<SnackbarOptions>>(), Arg.Any<string>());
        }

        public override async ValueTask DisposeAsync()
        {
            _presenter.Dispose();
            await _circuit.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
