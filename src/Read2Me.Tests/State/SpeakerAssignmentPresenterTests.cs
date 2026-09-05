using Microsoft.EntityFrameworkCore;
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
    /// The MudBlazor adapter's half of a speaker assignment (ADR 0007): which gesture becomes which
    /// mutation, what the reader is asked to confirm, what they are told afterwards, and the queue
    /// state a by-hand answer supersedes.
    /// <para>
    /// Everything below the adapter is real — a SQLite project, <see cref="BookMutations"/>, and the
    /// circuit's own <see cref="BookViewProjection"/> — because "the assign moved the rows the
    /// confirm promised" is a claim about the persisted Book. What the write reports is asserted in
    /// <c>SpeakerAttributionMutationTests</c>; how a Book View reconciles it, in
    /// <c>BookViewProjectionTests</c>.
    /// </para>
    /// </summary>
    public class SpeakerAssignmentPresenterTests : ProjectDbTestBase
    {
        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();

        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _circuit;
        private readonly ProjectFolderId _folder;
        private readonly ProjectReader _reader;
        private readonly BookViewProjection _projection;
        private readonly BookHierarchyPresenter _presenter;
        private readonly CharacterQueueService _characterQueue = new();
        private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
        private readonly ISnackbar _snackbar = Substitute.For<ISnackbar>();

        public SpeakerAssignmentPresenterTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _circuit = _root.CreateAsyncScope();

            _folder = new ProjectFolderId(FolderName);
            _reader = _circuit.ServiceProvider.GetRequiredService<ProjectReader>();

            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var audioQueue = new AudioQueueService();
            var ttsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            ttsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);

            var coordinator = new BookSelectionCoordinator(
                _reader, _characterQueue, audioQueue, ttsSettings, _snackbar,
                selectionState, audioSelectionState, new FakeAiPreflight());

            _projection = new BookViewProjection(
                new BookProjectLoader(_reader), _reader, _reader, _reader,
                _circuit.ServiceProvider.GetRequiredService<BookMutations>(),
                new BookTreeState(), selectionState, audioSelectionState, coordinator,
                new NullVoiceResolver(),
                _root.GetRequiredService<BookRevisionSequence>(),
                _circuit.ServiceProvider.GetRequiredService<ProjectDbSession>(),
                _root.GetRequiredService<EventBroadcaster<BookMutationReceipt>>(),
                NullLogger<BookViewProjection>.Instance);

            _presenter = new BookHierarchyPresenter(
                _reader, _projection, _circuit.ServiceProvider.GetRequiredService<CharacterResolver>(), new FakeBookUseCases(), selectionState, audioSelectionState, _dialogs, _snackbar,
                _characterQueue, new AudioReviewService(),
                new NodeStatusService(new FakeParagraphQueueProbe()));
        }

        // ── arrangement ──────────────────────────────────────────────────────

        /// <summary>
        /// One chapter of three Paragraphs: two with a dialog line and narration, and one of pure
        /// narration — the paragraph a bulk assign has to skip.
        /// </summary>
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
                        .AddNarration("n2", "she added."))
                    .AddParagraph("p3", p => p.AddNarration("n3", "Silence."))))
                .BuildAsync();
            return b;
        }

        /// <summary>Opens the Book View with the chapter expanded, so its Paragraphs are loaded.</summary>
        private async Task OpenWithChapterAsync(BookHierarchyBuilder b)
        {
            await _presenter.LoadAsync(_folder);
            await _presenter.SetNodeExpandedAsync(BookNodeLevel.Chapter, b.ChapterId("ch"), true);
        }

        private Task SelectAsync(BookHierarchyBuilder b, string paragraph) =>
            _presenter.ToggleParagraphAsync(
                b.ParagraphId(paragraph),
                new ParagraphSelection(b.VolumeId("vol"), b.PartId("vol"), b.ChapterId("ch")),
                on: true);

        private Paragraph Loaded(BookHierarchyBuilder b, string paragraph) =>
            _presenter.Paragraphs(b.ChapterId("ch"))!.Single(p => p.Id == b.ParagraphId(paragraph));

        private async Task<Guid?> SpeakerOfAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return (await db.ParagraphItems.FindAsync(itemId))!.CharacterId;
        }

        private void StubConfirm(bool confirmed)
        {
            var dialogRef = Substitute.For<IDialogReference>();
            dialogRef.Result.Returns(Task.FromResult<DialogResult?>(
                confirmed ? DialogResult.Ok(true) : DialogResult.Cancel()));

            _dialogs.ShowAsync<Read2Me.App.Shared.ConfirmDialog>(
                    Arg.Any<string>(), Arg.Any<DialogParameters<Read2Me.App.Shared.ConfirmDialog>>())
                .Returns(Task.FromResult(dialogRef));
        }

        private (string Title, string Message, string ConfirmText) CapturedConfirm()
        {
            var call = _dialogs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IDialogService.ShowAsync));
            var args = call.GetArguments();
            var parameters = (DialogParameters<Read2Me.App.Shared.ConfirmDialog>)args[1]!;
            return ((string)args[0]!, (string)parameters["Message"]!, (string)parameters["ConfirmText"]!);
        }

        private void StubAddCharacterDialog(string? name)
        {
            var dialogRef = Substitute.For<IDialogReference>();
            dialogRef.Result.Returns(Task.FromResult<DialogResult?>(
                name is null ? DialogResult.Cancel() : DialogResult.Ok(name)));

            _dialogs.ShowAsync<Read2Me.App.Shared.Characters.AddCharacterDialog>(Arg.Any<string>())
                .Returns(Task.FromResult(dialogRef));
        }

        // ── inventing a speaker from the chip menu ───────────────────────────

        [Fact]
        public async Task AddCharacter_CreatesThem_AndTheRosterOnScreenAlreadyKnows()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            StubAddCharacterDialog("  Carol  ");

            var newId = await _presenter.AddCharacterAsync(_folder);

            Assert.NotNull(newId);
            // No refresh of its own: the mutation's reconciliation republished the roster, which is
            // what the chip menu about to stamp this id renders from.
            Assert.Equal("Carol", _presenter.Characters.Single(c => c.Id == newId).Name);
        }

        /// <summary>
        /// Typing a name the Book already has creates nobody, and the gesture still has to answer with
        /// an id — the caller goes straight on to stamping the row with it.
        /// </summary>
        [Fact]
        public async Task AddCharacter_ANameTheRosterAlreadyAnswersTo_AnswersWithWhoeverGoesByIt()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            StubAddCharacterDialog("alice");

            Assert.Equal(AliceId, await _presenter.AddCharacterAsync(_folder));

            await using var verify = await OpenDbAsync();
            Assert.Equal(1, await verify.Characters.CountAsync(c => c.Name == "Alice"));
        }

        [Fact]
        public async Task AddCharacter_Cancelled_WritesNothing()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            StubAddCharacterDialog(null);

            Assert.Null(await _presenter.AddCharacterAsync(_folder));
        }

        // ── the chip front door ──────────────────────────────────────────────

        [Fact]
        public async Task AssignCharacter_ArmedAndInSelection_FansOutAcrossTheWholeSelection()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");
            await SelectAsync(b, "p2");
            await _presenter.SetBulkAssignAsync(true);
            StubConfirm(confirmed: true);

            // The chip fired on p1; the gesture is about the selection, not the row.
            await _presenter.AssignCharacterAsync(_folder, Loaded(b, "p1"), null, BobId);

            Assert.Equal(BobId, await SpeakerOfAsync(b.ItemId("d1")));
            Assert.Equal(BobId, await SpeakerOfAsync(b.ItemId("d2")));
        }

        [Fact]
        public async Task AssignCharacter_ArmedButRowOutsideTheSelection_AssignsThatRowOnly()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");
            await _presenter.SetBulkAssignAsync(true);

            await _presenter.AssignCharacterAsync(_folder, Loaded(b, "p2"), null, BobId);

            Assert.Equal(BobId, await SpeakerOfAsync(b.ItemId("d2")));
            Assert.Equal(AliceId, await SpeakerOfAsync(b.ItemId("d1")));
            // A single assign asks nothing: only the fan-out is behind a confirm.
            Assert.Empty(_dialogs.ReceivedCalls());
        }

        [Fact]
        public async Task AssignCharacter_Disarmed_ItemChip_StampsThatItemAlone()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");

            var paragraph = Loaded(b, "p1");
            await _presenter.AssignCharacterAsync(
                _folder, paragraph, paragraph.Items.Single(i => i.Id == b.ItemId("n1")), BobId);

            // The narration item itself moved — an item chip stamps any speaker on any speech item
            // (ADR-0006) — and its dialog neighbour did not.
            Assert.Equal(BobId, await SpeakerOfAsync(b.ItemId("n1")));
            Assert.Equal(AliceId, await SpeakerOfAsync(b.ItemId("d1")));
        }

        [Fact]
        public async Task SetItemCharacterAsync_ClearsTheParagraphsQueueOutcome()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);

            var queued = new QueuedParagraph(
                _folder, b.ParagraphId("p1"), "preview", b.ChapterId("ch"), b.PartId("vol"), b.VolumeId("vol"));
            _characterQueue.Enqueue([queued]);
            _characterQueue.MarkProcessing(queued);
            _characterQueue.Apply(queued, new Disposition.Failed("boom"));
            Assert.NotNull(_characterQueue.OutcomeOf(_folder, b.ParagraphId("p1")));

            await _presenter.SetItemCharacterAsync(
                _folder, Loaded(b, "p1").Items.Single(i => i.Id == b.ItemId("d1")), BobId);

            // The user has just answered the question the failed attempt was about.
            Assert.Null(_characterQueue.OutcomeOf(_folder, b.ParagraphId("p1")));
        }

        // ── the bulk apply ───────────────────────────────────────────────────

        [Fact]
        public async Task AssignCharacterToSelection_ConfirmQuotesTheFigures_AndNamesTheSkippedParagraphs()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");
            await SelectAsync(b, "p2");
            await SelectAsync(b, "p3");
            StubConfirm(confirmed: true);

            await _presenter.AssignCharacterToSelectionAsync(_folder, BobId);

            var (title, message, confirmText) = CapturedConfirm();
            Assert.Equal("Assign Bob to selection", title);
            Assert.Equal("Assign", confirmText);
            Assert.Contains("2 dialog lines in 2 paragraphs", message);
            // Pluralisation is noun-suffix only, the idiom the confirm wordings are written in.
            Assert.Contains("1 selected paragraph", message);
            Assert.Contains("no dialog and stay unchanged.", message);
        }

        [Fact]
        public async Task AssignCharacterToSelection_Confirmed_WritesAndReportsWhatItMoved()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");
            await SelectAsync(b, "p2");
            StubConfirm(confirmed: true);

            await _presenter.AssignCharacterToSelectionAsync(_folder, BobId);

            Assert.Equal(BobId, await SpeakerOfAsync(b.ItemId("d1")));
            _snackbar.Received(1).Add(
                "Assigned Bob to 2 lines in 2 paragraphs.", Severity.Success,
                Arg.Any<Action<SnackbarOptions>>(), Arg.Any<string>());
        }

        [Fact]
        public async Task AssignCharacterToSelection_KeepsTheSelectionWhenNothingLeftTheRollUp()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");
            StubConfirm(confirmed: true);

            // Swapping one character for another leaves p1 a Character paragraph, so the dock bar
            // stays up for the next gesture.
            await _presenter.AssignCharacterToSelectionAsync(_folder, BobId);

            Assert.Equal(1, _presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task AssignCharacterToSelection_NoDialogInTheSelection_SaysSoAndWritesNothing()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p3");

            await _presenter.AssignCharacterToSelectionAsync(_folder, BobId);

            _snackbar.Received(1).Add(
                "No dialog in the selection — nothing to assign.", Severity.Info,
                Arg.Any<Action<SnackbarOptions>>(), Arg.Any<string>());
            Assert.Empty(_dialogs.ReceivedCalls());
            Assert.Equal(ProjectDbContext.NarratorId, await SpeakerOfAsync(b.ItemId("n3")));
        }

        [Fact]
        public async Task AssignCharacterToSelection_CancelledConfirm_WritesNothingAndKeepsTheSelection()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");
            await _presenter.SetBulkAssignAsync(true);
            StubConfirm(confirmed: false);

            await _presenter.AssignCharacterToSelectionAsync(_folder, BobId);

            Assert.Equal(AliceId, await SpeakerOfAsync(b.ItemId("d1")));
            Assert.Equal(1, _presenter.Selection.SelectedParagraphCount);
            Assert.True(_presenter.Selection.BulkMode);
        }

        [Fact]
        public async Task AssignCharacterToSelection_Clearing_IsWordedAsAClearThroughout()
        {
            var b = await SeedAsync();
            await OpenWithChapterAsync(b);
            await SelectAsync(b, "p1");
            StubConfirm(confirmed: true);

            await _presenter.AssignCharacterToSelectionAsync(_folder, null);

            var (title, message, confirmText) = CapturedConfirm();
            Assert.Equal("Clear speakers in selection", title);
            Assert.Equal("Clear", confirmText);
            Assert.Contains("lose their speaker", message);
            Assert.Null(await SpeakerOfAsync(b.ItemId("d1")));
            _snackbar.Received(1).Add(
                "Cleared speakers on 1 lines in 1 paragraphs.", Severity.Success,
                Arg.Any<Action<SnackbarOptions>>(), Arg.Any<string>());
        }

        /// <summary>Voice previews are the projection's concern; this file is about the adapter.</summary>
        private sealed class NullVoiceResolver : IVoiceResolver
        {
            public Task<IReadOnlyDictionary<Guid, Guid?>> ResolveAsync(
                ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(itemIds.ToDictionary(id => id, _ => (Guid?)null));

            public Task<IReadOnlyDictionary<Guid, string?>> ResolveNamesAsync(
                ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyDictionary<Guid, string?>>(itemIds.ToDictionary(id => id, _ => (string?)null));
        }

        public override async ValueTask DisposeAsync()
        {
            _projection.Dispose();
            await _circuit.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
