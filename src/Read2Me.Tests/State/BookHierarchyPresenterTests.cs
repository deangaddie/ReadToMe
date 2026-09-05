using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.App.State.Projection;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Queueing;
using Read2Me.Services.UseCases;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.State
{
    // Fake BookUseCases: controllable import results without real dependencies.
    internal class FakeBookUseCases : BookUseCases
    {
        private Result _result = Result.Ok();

        public FakeBookUseCases() : base(null!, null!, null!, null!) { }

        public void SetResult(Result r) => _result = r;

        public override Task<Result> ImportAsync(string folderName, bool reread = false, CancellationToken ct = default)
            => Task.FromResult(_result);

        public override Task<Result> ImportManuallyAsync(string folderName, ManualReadOptions options, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    public class BookHierarchyPresenterTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private record Context(
            BookHierarchyPresenter Presenter,
            BookViewProjection Projection,
            IProjectReader Reader,
            IBookProjectLoader Loader,
            IBookCommandHandler CommandHandler,
            FakeBookUseCases BookUseCases,
            BookTreeState TreeState,
            AudioReviewService AudioReviews,
            NodeStatusService NodeStatus,
            FakeVoiceResolver VoiceResolver,
            CharacterQueueService CharacterQueue,
            AudioQueueService AudioQueue,
            List<Character> Roster,
            List<ParagraphStatusSeedRow> NodeStatusSeed,
            IDialogService Dialogs,
            ISnackbar Snackbar);

        private static BookProjectSnapshot EmptySnapshot(
            IReadOnlyDictionary<Guid, int>? nodeCounts = null,
            HashSet<Guid>? selectableNodes = null,
            IReadOnlyList<Volume>? volumes = null,
            List<Character>? characters = null,
            bool hasContent = false,
            IReadOnlyDictionary<Guid, int>? audioNodeCounts = null,
            List<(Guid ParagraphItemId, AudioReviewInfo Info)>? audioReviews = null,
            IReadOnlyList<ParagraphStatusSeedRow>? nodeStatusSeed = null) =>
            new(
                Filename: null,
                HasContent: hasContent,
                Volumes: volumes ?? [],
                Characters: characters ?? [],
                TotalParts: 0,
                TotalChapters: 0,
                SelectableNodeIds: selectableNodes ?? [],
                NodeCharacterParagraphCounts: nodeCounts ?? new Dictionary<Guid, int>(),
                NarratorOnlyMode: false,
                AudioNodeCounts: audioNodeCounts ?? new Dictionary<Guid, int>(),
                AudioReviews: audioReviews ?? [],
                NodeStatusSeed: nodeStatusSeed ?? []
            );

        /// <summary>
        /// The presenter over a real <see cref="BookViewProjection"/> — the seam it adapts — with the
        /// reads behind the projection substituted. Nothing the Book View renders is presenter state
        /// any more, so a test arranges the reads and then opens.
        /// </summary>
        private static Context Create(IReadOnlyDictionary<Guid, int>? nodeCounts = null)
        {
            var reader = Substitute.For<IProjectReader>();
            var loader = Substitute.For<IBookProjectLoader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            // Read on every build rather than captured once, so a test that adds to the roster sees
            // it after the next rebuild — the add-a-character path, which no longer patches a list.
            var roster = new List<Character>();
            var seed = new List<ParagraphStatusSeedRow>();
            loader.LoadSnapshotAsync(Arg.Any<ProjectFolderId>(), Arg.Any<CancellationToken>())
                .Returns(_ => EmptySnapshot(nodeCounts, characters: roster, nodeStatusSeed: seed));

            reader.GetCharacterParagraphsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<BookNodeLevel>(), Arg.Any<Guid>(), Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>());

            reader.GetAudioItemRefsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<BookNodeLevel>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new List<AudioItemRef>());

            reader.GetCharactersAsync(Arg.Any<ProjectFolderId>())
                .Returns(new List<Character>());

            // A speaker change reseeds the derived counts from here (ADR-0006). Default to the same
            // counts the snapshot carries, so nothing "moves" unless a test says so.
            reader.GetBookOverviewAsync(Arg.Any<ProjectFolderId>())
                .Returns(_ => new BookOverview(
                    null, true, [], [], 0, 0,
                    [.. (nodeCounts ?? new Dictionary<Guid, int>()).Keys],
                    nodeCounts ?? new Dictionary<Guid, int>()));

            var treeState = new BookTreeState();
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var characterQueue = new CharacterQueueService();
            var snackbar = Substitute.For<ISnackbar>();
            var paragraphTtsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            paragraphTtsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);
            var audioReviews = new AudioReviewService();
            var nodeStatus = new NodeStatusService(new FakeParagraphQueueProbe());
            var voiceResolver = new FakeVoiceResolver();
            var audioQueue = new AudioQueueService();
            var coordinator = new BookSelectionCoordinator(reader, characterQueue, audioQueue, paragraphTtsSettings, snackbar, selectionState, audioSelectionState, new FakeAiPreflight());
            // No BookMutations and no session: every mutation this file still covers goes through the
            // legacy command handler, and nothing here converges on another circuit. The migrated
            // families are proved on BookViewProjection, where a real write side is the point.
            var projection = new BookViewProjection(
                loader, reader, reader, reader, mutations: null!, treeState, selectionState,
                audioSelectionState, coordinator, voiceResolver, new BookRevisionSequence(), session: null!,
                new EventBroadcaster<BookMutationReceipt>(),
                NullLogger<BookViewProjection>.Instance);
            var presenter = new BookHierarchyPresenter(reader, projection, commandHandler, bookUseCases, selectionState, audioSelectionState, dialogService, snackbar, characterQueue, audioReviews, nodeStatus);
            return new Context(presenter, projection, reader, loader, commandHandler, bookUseCases, treeState, audioReviews, nodeStatus, voiceResolver, characterQueue, audioQueue, roster, seed, dialogService, snackbar);
        }

        /// <summary>
        /// The Book's status rows, as the loader reports them on every build. Mutate the list to stage
        /// what a write changed: the rebuild that ends the write reads it back.
        /// </summary>
        private static List<ParagraphStatusSeedRow> StubNodeStatus(
            Context ctx, params ParagraphStatusSeedRow[] rows)
        {
            ctx.NodeStatusSeed.Clear();
            ctx.NodeStatusSeed.AddRange(rows);
            return ctx.NodeStatusSeed;
        }

        /// <summary>The one volume, part and chapter of the smallest Book a chapter can be opened in.</summary>
        private readonly record struct OneChapterBook(Guid VolumeId, Guid PartId, Guid ChapterId);

        private static OneChapterBook NewOneChapterBook() =>
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        /// <summary>
        /// Opens a Book of one volume, one part and one chapter, and expands that chapter so its
        /// paragraphs are loaded. The projection opens a lone volume and a lone part itself, so the
        /// chapter is the only gesture a test has to make.
        /// </summary>
        private static async Task OpenWithChapterAsync(
            Context ctx,
            OneChapterBook book,
            IReadOnlyList<Paragraph> paragraphs,
            IReadOnlyList<ParagraphStatusSeedRow>? nodeStatusSeed = null,
            IReadOnlyDictionary<Guid, int>? nodeCounts = null,
            List<(Guid ParagraphItemId, AudioReviewInfo Info)>? audioReviews = null)
        {
            var volume = new Volume { Id = book.VolumeId, Order = "a" };
            if (nodeStatusSeed is not null) StubNodeStatus(ctx, [.. nodeStatusSeed]);

            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(_ => EmptySnapshot(
                    hasContent: true,
                    volumes: [volume],
                    characters: ctx.Roster,
                    nodeCounts: nodeCounts,
                    audioReviews: audioReviews,
                    nodeStatusSeed: ctx.NodeStatusSeed));

            ctx.Reader.GetChildrenAsync(Folder, BookNodeLevel.Volume, book.VolumeId)
                .Returns(new HierarchyChildren([new Part { Id = book.PartId, Order = "a" }], null, null));
            ctx.Reader.GetChildrenAsync(Folder, BookNodeLevel.Part, book.PartId)
                .Returns(new HierarchyChildren(null, [new Chapter { Id = book.ChapterId, Order = "a" }], null));
            ctx.Reader.GetChildrenAsync(Folder, BookNodeLevel.Chapter, book.ChapterId)
                .Returns(new HierarchyChildren(null, null, [.. paragraphs]));

            await ctx.Presenter.LoadAsync(Folder);
            await ctx.Presenter.SetNodeExpandedAsync(BookNodeLevel.Chapter, book.ChapterId, expanded: true);
        }

        // ---------------------------------------------------------------
        // LoadAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadAsync_NoContent_HasContentFalse_VolumesEmpty()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            Assert.False(ctx.Presenter.HasContent);
            Assert.Empty(ctx.Presenter.Volumes);
        }

        [Fact]
        public async Task LoadAsync_WithContent_HasContentTrue_LoadsVolumes()
        {
            var ctx = Create();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol1", Order = "a" };
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(hasContent: true, volumes: [vol]));
            ctx.Reader.GetChildrenAsync(Folder, BookNodeLevel.Volume, vol.Id)
                .Returns(new HierarchyChildren(new List<Part>(), null, null));

            await ctx.Presenter.LoadAsync(Folder);

            Assert.True(ctx.Presenter.HasContent);
            Assert.Single(ctx.Presenter.Volumes);
        }

        [Fact]
        public async Task LoadAsync_IsLoading_FalseAfterComplete()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            Assert.False(ctx.Presenter.IsLoading);
        }

        // ---------------------------------------------------------------
        // ReadBookAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task ReadBookAsync_Success_ErrorIsNull()
        {
            var ctx = Create();
            ctx.BookUseCases.SetResult(Result.Ok());

            await ctx.Presenter.ReadBookAsync(Folder);

            Assert.Null(ctx.Presenter.Error);
        }

        [Fact]
        public async Task ReadBookAsync_Failure_SetsError()
        {
            var ctx = Create();
            ctx.BookUseCases.SetResult(Result.Fail("Import failed"));

            await ctx.Presenter.ReadBookAsync(Folder);

            Assert.Equal("Import failed", ctx.Presenter.Error);
        }

        [Fact]
        public async Task ReadBookAsync_IsBusy_FalseAfterComplete()
        {
            var ctx = Create();
            ctx.BookUseCases.SetResult(Result.Ok());

            await ctx.Presenter.ReadBookAsync(Folder);

            Assert.False(ctx.Presenter.IsBusy);
        }

        // ---------------------------------------------------------------
        // ConfirmReread / RequestConfirmReread / CancelConfirmReread
        // ---------------------------------------------------------------

        [Fact]
        public void RequestConfirmReread_SetsConfirmRereadTrue()
        {
            var ctx = Create();
            ctx.Presenter.RequestConfirmReread();
            Assert.True(ctx.Presenter.ConfirmReread);
        }

        [Fact]
        public void CancelConfirmReread_SetsConfirmRereadFalse()
        {
            var ctx = Create();
            ctx.Presenter.RequestConfirmReread();
            ctx.Presenter.CancelConfirmReread();
            Assert.False(ctx.Presenter.ConfirmReread);
        }

        // ---------------------------------------------------------------
        // StateChanged event
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadAsync_RaisesStateChanged()
        {
            var ctx = Create();
            bool raised = false;
            ctx.Presenter.StateChanged += () => raised = true;

            await ctx.Presenter.LoadAsync(Folder);

            Assert.True(raised);
        }

        // ---------------------------------------------------------------
        // Selection: LoadAsync exposes FolderSelection
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadAsync_ExposesSelectionForFolder()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            Assert.NotNull(ctx.Presenter.Selection);
        }

        [Fact]
        public async Task LoadAsync_SameFolder_DoesNotClearExistingSelection()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            ctx.Presenter.Selection.AddParagraph(Guid.NewGuid(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);

            await ctx.Presenter.LoadAsync(Folder);

            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task LoadAsync_DifferentFolder_ClearsPreviousFolderSelection()
        {
            var ctx = Create();
            var other = new ProjectFolderId("other-book");

            ctx.Loader.LoadSnapshotAsync(other, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot());

            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            ctx.Presenter.Selection.AddParagraph(Guid.NewGuid(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);

            await ctx.Presenter.LoadAsync(other);
            await ctx.Presenter.LoadAsync(Folder);
            Assert.Equal(0, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        // ---------------------------------------------------------------
        // Selection: ToggleParagraphAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task ToggleParagraphAsync_On_AddsToSelection()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var pId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            await ctx.Presenter.ToggleParagraphAsync(pId, new ParagraphSelection(volId, ptId, chId), on: true);

            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(pId));
        }

        [Fact]
        public async Task ToggleParagraphAsync_Off_RemovesFromSelection()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var pId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            await ctx.Presenter.ToggleParagraphAsync(pId, new ParagraphSelection(volId, ptId, chId), on: true);
            await ctx.Presenter.ToggleParagraphAsync(pId, new ParagraphSelection(volId, ptId, chId), on: false);

            Assert.False(ctx.Presenter.Selection.IsParagraphSelected(pId));
        }

        [Fact]
        public async Task ToggleParagraphAsync_CompleteChapter_ChapterChecked()
        {
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var counts = new Dictionary<Guid, int> { [chId] = 1 };
            var ctx = Create(counts);
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeCounts: counts));
            await ctx.Presenter.LoadAsync(Folder);

            var pId = Guid.NewGuid();
            await ctx.Presenter.ToggleParagraphAsync(pId, new ParagraphSelection(volId, ptId, chId), on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Chapter, chId));
        }

        // ---------------------------------------------------------------
        // Selection: SetNodeAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetNodeAsync_On_AddsAllParagraphsUnderNode()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId1 = Guid.NewGuid(); var pId2 = Guid.NewGuid();

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId1, chId, ptId, volId),
                    new CharacterParagraphRef(pId2, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Chapter, chId, on: true);

            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(pId1));
            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(pId2));
        }

        [Fact]
        public async Task SetNodeAsync_Off_RemovesAllParagraphsUnderNode()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Chapter, chId, on: true);
            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Chapter, chId, on: false);

            Assert.False(ctx.Presenter.Selection.IsParagraphSelected(pId));
        }

        [Fact]
        public async Task SetNodeAsync_Volume_On_MarksDescendantChaptersAndPartsChecked()
        {
            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId1 = Guid.NewGuid(); var pId2 = Guid.NewGuid();

            var counts = new Dictionary<Guid, int>
            {
                [volId] = 2,
                [ptId] = 2,
                [chId] = 2,
            };
            var ctx = Create(counts);
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeCounts: counts));
            await ctx.Presenter.LoadAsync(Folder);

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Volume, volId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId1, chId, ptId, volId),
                    new CharacterParagraphRef(pId2, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Volume, volId, on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Volume, volId));
            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Part, ptId));
            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Chapter, chId));
        }

        [Fact]
        public async Task SetNodeAsync_Part_On_MarksDescendantChaptersChecked()
        {
            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            var counts = new Dictionary<Guid, int> { [chId] = 1 };
            var ctx = Create(counts);
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeCounts: counts));
            await ctx.Presenter.LoadAsync(Folder);

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Part, ptId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Chapter, chId));
        }

        [Fact]
        public async Task SetNodeAsync_Part_Off_ClearsDescendantChapterNodes()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Part, ptId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: false);

            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Chapter, chId));
            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Part, ptId));
        }

        [Fact]
        public async Task SetNodeAsync_Volume_Off_ClearsDescendantPartAndChapterNodes()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId1 = Guid.NewGuid(); var pId2 = Guid.NewGuid();

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Volume, volId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId1, chId, ptId, volId),
                    new CharacterParagraphRef(pId2, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Volume, volId, on: true);
            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Volume, volId, on: false);

            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Volume, volId));
            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Part, ptId));
            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Chapter, chId));
            Assert.False(ctx.Presenter.Selection.IsParagraphSelected(pId1));
            Assert.False(ctx.Presenter.Selection.IsParagraphSelected(pId2));
        }

        [Fact]
        public async Task SetNodeAsync_Part_Off_ClearsParagraphsAndPartNode()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Part, ptId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: false);

            Assert.False(ctx.Presenter.Selection.IsParagraphSelected(pId));
            Assert.Equal(0, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task SetNodeAsync_Part_OnOffOn_ReselectsCleanly()
        {
            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            var counts = new Dictionary<Guid, int> { [chId] = 1 };
            var ctx = Create(counts);
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeCounts: counts));
            await ctx.Presenter.LoadAsync(Folder);

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Part, ptId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: false);
            await ctx.Presenter.SetNodeAsync(BookNodeLevel.Part, ptId, on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(BookNodeLevel.Chapter, chId));
            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(pId));
        }

        // ---------------------------------------------------------------
        // Selectable nodes
        // ---------------------------------------------------------------

        [Fact]
        public async Task IsNodeSelectable_NodeWithCharacterParagraphs_True()
        {
            var ctx = Create();
            var nodeId = Guid.NewGuid();
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(hasContent: true, selectableNodes: [nodeId]));

            await ctx.Presenter.LoadAsync(Folder);

            Assert.True(ctx.Presenter.IsNodeSelectable(nodeId));
        }

        [Fact]
        public async Task IsNodeSelectable_NodeWithoutCharacterParagraphs_False()
        {
            var ctx = Create();
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(hasContent: true));

            await ctx.Presenter.LoadAsync(Folder);

            Assert.False(ctx.Presenter.IsNodeSelectable(Guid.NewGuid()));
        }

        // ---------------------------------------------------------------
        // ResetAndLoadAsync clears selection
        // ---------------------------------------------------------------

        [Fact]
        public async Task ResetAndLoadAsync_ClearsSelection()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            ctx.Presenter.Selection.AddParagraph(Guid.NewGuid(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);

            await ctx.Presenter.ResetAndLoadAsync(Folder);

            Assert.Equal(0, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        // ---------------------------------------------------------------
        // NoteItemTextEditedAsync — a text edit returns the item to pre-generation state
        // ---------------------------------------------------------------
        /// <summary>
        /// A Book whose one item carries a needs-review verdict, in the database seed and in the
        /// in-memory mirror the tree renders from.
        /// </summary>
        private static async Task<(Context ctx, Guid itemId, OneChapterBook book)>
            CreateWithReviewSeedRowAsync()
        {
            var book = NewOneChapterBook();
            var paraId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var para = new Paragraph
            {
                Id = paraId,
                ChapterId = book.ChapterId,
                Items =
                [
                    new ParagraphItem { Id = itemId, ParagraphId = paraId, ItemType = ParagraphItemType.Speech, Order = "a" },
                ]
            };

            var ctx = Create();
            await OpenWithChapterAsync(ctx, book, [para], nodeStatusSeed:
            [
                new(paraId, book.ChapterId, book.PartId, book.VolumeId, Unattributed: 0, MissingAudio: 0, Review: 1),
            ]);

            ctx.AudioReviews.Set(Folder, itemId, new AudioReviewInfo(
                Read2Me.Core.Models.AudioReviewState.NeedsReview,
                NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.3, VerifyReason: "WER too high",
                Transcript: "t", OriginalTextSnapshot: "o"));

            return (ctx, itemId, book);
        }


        /// <summary>
        /// The row has to go back to Generatable without a reload: while it still shows a WAV its
        /// audio checkbox stays disabled, a "select needs audio" pass keeps skipping it, and the
        /// chapter's audio-remaining badge reads one too low.
        /// </summary>
        [Fact]
        public async Task NoteItemTextEditedAsync_ClearsAudio_DropsTheReview_AndRaisesTheAudioBadge()
        {
            var (ctx, itemId, book) = await CreateWithReviewSeedRowAsync();
            var item = ctx.Projection.Snapshot!.Branches.AllParagraphs().First(p => p.Items.Any(i => i.Id == itemId)).Items.Single(i => i.Id == itemId);
            item.AudioFileName = "item.wav";

            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).Review);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);

            // The handler has already cleared the WAV and deleted the verdict; the reseed reads that back.
            ctx.Reader.GetNodeStatusSeedAsync(Folder).Returns<IReadOnlyList<ParagraphStatusSeedRow>>(
            [
                new(item.ParagraphId, book.ChapterId, book.PartId, book.VolumeId, Unattributed: 0, MissingAudio: 1, Review: 0),
            ]);

            await ctx.Presenter.NoteItemTextEditedAsync(Folder, item);

            Assert.Null(item.AudioFileName);
            Assert.Null(ctx.AudioReviews.ReviewOf(Folder, itemId));
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.VolumeId).AudioRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).Review);
        }

        // ---------------------------------------------------------------
        // SetAudioNodeAsync needsAudioOnly (issue 0001)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetAudioNodeAsync_NeedsAudioOnly_ForwardsFlagAndSelectsReturnedItems()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var itemId1 = Guid.NewGuid(); var paraId1 = Guid.NewGuid();
            var itemId2 = Guid.NewGuid(); var paraId2 = Guid.NewGuid();

            ctx.Reader.GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, true)
                .Returns(new List<AudioItemRef>
                {
                    new AudioItemRef(itemId1, paraId1, chId, ptId, volId),
                    new AudioItemRef(itemId2, paraId2, chId, ptId, volId),
                });

            await ctx.Presenter.SetAudioNodeAsync(BookNodeLevel.Chapter, chId, on: true, needsAudioOnly: true);

            Assert.True(ctx.Presenter.AudioSelection.IsItemSelected(itemId1));
            Assert.True(ctx.Presenter.AudioSelection.IsItemSelected(itemId2));
            await ctx.Reader.Received(1).GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, true);
        }

        [Fact]
        public async Task SetAudioNodeAsync_Default_PassesFalseToReader()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var chId = Guid.NewGuid();
            ctx.Reader.GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, false)
                .Returns(new List<AudioItemRef>());

            await ctx.Presenter.SetAudioNodeAsync(BookNodeLevel.Chapter, chId, on: true);

            await ctx.Reader.Received(1).GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, false);
        }

        [Fact]
        public async Task SetAudioNodeAsync_NeedsAudioOnly_NodeStateIsIndeterminate_WhenSubsetSelected()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var itemId1 = Guid.NewGuid(); var paraId1 = Guid.NewGuid();

            // Denominator = 3 items total under the chapter; needsAudioOnly returns only 1
            ctx.Presenter.AudioSelection.SetCounts(new Dictionary<Guid, int> { [chId] = 3 });

            ctx.Reader.GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, true)
                .Returns(new List<AudioItemRef>
                {
                    new AudioItemRef(itemId1, paraId1, chId, ptId, volId),
                });

            await ctx.Presenter.SetAudioNodeAsync(BookNodeLevel.Chapter, chId, on: true, needsAudioOnly: true);

            Assert.Equal(TriState.Indeterminate, ctx.Presenter.AudioSelection.NodeState(BookNodeLevel.Chapter, chId));
        }

        // ---------------------------------------------------------------
        // ViewMode setter (issue 003)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetViewMode_NewValue_ClearsFolderSelection()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            await ctx.Presenter.ToggleParagraphAsync(Guid.NewGuid(), new ParagraphSelection(volId, ptId, chId), on: true);
            Assert.Equal(1, ctx.Presenter.SelectedParagraphCount);

            await ctx.Presenter.SetViewModeAsync(BookViewMode.SplitAudio);

            Assert.Equal(0, ctx.Presenter.SelectedParagraphCount);
        }

        [Fact]
        public async Task SetViewMode_NewValue_ClearsAudioItemSelection()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid(); var paraId = Guid.NewGuid();
            await ctx.Presenter.ToggleAudioItemAsync(new AudioItemRef(Guid.NewGuid(), paraId, chId, ptId, volId), on: true);
            Assert.Equal(1, ctx.Presenter.SelectedAudioItemCount);

            await ctx.Presenter.SetViewModeAsync(BookViewMode.SplitAudio);

            Assert.Equal(0, ctx.Presenter.SelectedAudioItemCount);
        }

        [Fact]
        public async Task SetViewMode_NewValue_FiresStateChanged()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            int fired = 0;
            ctx.Presenter.StateChanged += () => fired++;

            await ctx.Presenter.SetViewModeAsync(BookViewMode.SplitAudio);

            Assert.Equal(1, fired);
        }

        [Fact]
        public async Task SetViewMode_SameValue_DoesNotFireStateChanged()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            int fired = 0;
            ctx.Presenter.StateChanged += () => fired++;

            await ctx.Presenter.SetViewModeAsync(BookViewMode.Combined); // already Combined

            Assert.Equal(0, fired);
        }

        // ---------------------------------------------------------------
        // Voice previews — resolved with the snapshot, for what it loaded
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadAsync_NamesTheVoiceOfEveryLoadedItem()
        {
            var ctx = Create();
            var book = NewOneChapterBook();
            var paraId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var para = new Paragraph
            {
                Id = paraId,
                ChapterId = book.ChapterId,
                Items = [new ParagraphItem { Id = itemId, ParagraphId = paraId, ItemType = ParagraphItemType.Speech, Order = "a" }],
            };
            ctx.VoiceResolver.SetName(itemId, "Alice Voice");

            await OpenWithChapterAsync(ctx, book, [para]);

            Assert.Equal("Alice Voice", ctx.Presenter.ResolvedVoiceName(itemId));
        }

        [Fact]
        public async Task ResolvedVoiceName_ItemNoBranchHasLoaded_IsNull()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            Assert.Null(ctx.Presenter.ResolvedVoiceName(Guid.NewGuid()));
        }

        [Fact]
        public async Task SwitchingViewMode_ReResolvesTheVoicePreviews()
        {
            // Voice rules can be edited on another tab: entering the audio view has to name the Voice
            // the queue would use now, not the one the last snapshot was built with.
            var ctx = Create();
            var book = NewOneChapterBook();
            var paraId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var para = new Paragraph
            {
                Id = paraId,
                ChapterId = book.ChapterId,
                Items = [new ParagraphItem { Id = itemId, ParagraphId = paraId, ItemType = ParagraphItemType.Speech, Order = "a" }],
            };
            ctx.VoiceResolver.SetName(itemId, "First");
            await OpenWithChapterAsync(ctx, book, [para]);

            ctx.VoiceResolver.SetName(itemId, "Changed");
            await ctx.Presenter.SetViewModeAsync(BookViewMode.SplitAudio);

            Assert.Equal("Changed", ctx.Presenter.ResolvedVoiceName(itemId));
        }

        // ---------------------------------------------------------------
        // Expansion — an intent, answered by a snapshot carrying the branch
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetNodeExpandedAsync_Chapter_PublishesItsParagraphs()
        {
            var ctx = Create();
            var book = NewOneChapterBook();
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = book.ChapterId };

            await OpenWithChapterAsync(ctx, book, [para]);

            Assert.True(ctx.Presenter.IsExpanded(BookNodeLevel.Chapter, book.ChapterId));
            Assert.Equal(para.Id, Assert.Single(ctx.Presenter.Paragraphs(book.ChapterId)!).Id);
        }

        [Fact]
        public async Task SetNodeExpandedAsync_Collapsing_DropsTheBranchAndTheIntent()
        {
            var ctx = Create();
            var book = NewOneChapterBook();
            await OpenWithChapterAsync(ctx, book, [new Paragraph { Id = Guid.NewGuid(), ChapterId = book.ChapterId }]);

            await ctx.Presenter.SetNodeExpandedAsync(BookNodeLevel.Chapter, book.ChapterId, expanded: false);

            Assert.False(ctx.Presenter.IsExpanded(BookNodeLevel.Chapter, book.ChapterId));
            Assert.Null(ctx.Presenter.Paragraphs(book.ChapterId));
        }

        [Fact]
        public async Task SetNodeExpandedAsync_TakesTheSpinnerDownWithARepaintOfItsOwn()
        {
            // The snapshot that answers the gesture is published while the spinner is still up, so a
            // view repainting only on that event would be left rendering the spinner over the branch.
            var ctx = Create();
            var book = NewOneChapterBook();
            await OpenWithChapterAsync(ctx, book, []);
            await ctx.Presenter.SetNodeExpandedAsync(BookNodeLevel.Chapter, book.ChapterId, expanded: false);

            var spinnerAtEachRepaint = new List<bool>();
            ctx.Presenter.StateChanged += () => spinnerAtEachRepaint.Add(ctx.Presenter.IsExpanding(book.ChapterId));

            await ctx.Presenter.SetNodeExpandedAsync(BookNodeLevel.Chapter, book.ChapterId, expanded: true);

            Assert.False(ctx.Presenter.IsExpanding(book.ChapterId));
            Assert.False(spinnerAtEachRepaint[^1]);
        }

        [Fact]
        public async Task SetNodeExpandedAsync_AlreadyExpanded_PublishesNothing()
        {
            var ctx = Create();
            var book = NewOneChapterBook();
            await OpenWithChapterAsync(ctx, book, [new Paragraph { Id = Guid.NewGuid(), ChapterId = book.ChapterId }]);

            var published = ctx.Projection.Snapshot!;
            await ctx.Presenter.SetNodeExpandedAsync(BookNodeLevel.Chapter, book.ChapterId, expanded: true);

            Assert.Same(published, ctx.Projection.Snapshot);
        }

        // ---------------------------------------------------------------
        // Bulk mode force-off while the character queue is busy
        // ---------------------------------------------------------------

        private static QueuedParagraph AnyQueuedParagraph() =>
            new(Folder, Guid.NewGuid(), "preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        [Fact]
        public async Task CharacterQueueGoesBusy_DisarmsBulkMode()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            await ctx.Presenter.SetBulkAssignAsync(true);

            ctx.CharacterQueue.Enqueue([AnyQueuedParagraph()]);

            Assert.False(ctx.Presenter.Selection.BulkMode);
        }

        [Fact]
        public async Task CharacterQueueChangedWhileIdle_DoesNotDisarmBulkMode()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            await ctx.Presenter.SetBulkAssignAsync(true);

            // CancelAll on an empty queue raises Changed with an idle snapshot.
            ctx.CharacterQueue.CancelAll();

            Assert.True(ctx.Presenter.Selection.BulkMode);
        }

        [Fact]
        public async Task CharacterQueueGoesIdle_DoesNotReArmBulkMode()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            await ctx.Presenter.SetBulkAssignAsync(true);
            ctx.CharacterQueue.Enqueue([AnyQueuedParagraph()]);

            ctx.CharacterQueue.CancelAll();

            Assert.False(ctx.Presenter.Selection.BulkMode);
        }

        [Fact]
        public async Task Dispose_UnsubscribesFromCharacterQueue()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            var selection = ctx.Presenter.Selection;
            ctx.Presenter.Dispose();

            selection.BulkMode = true;
            ctx.CharacterQueue.Enqueue([AnyQueuedParagraph()]);

            Assert.True(selection.BulkMode);
        }
    }
}
