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
            EventBroadcaster<ParagraphItemsChanged> Events,
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
            var events = new EventBroadcaster<ParagraphItemsChanged>();
            var coordinator = new BookSelectionCoordinator(reader, characterQueue, audioQueue, paragraphTtsSettings, snackbar, selectionState, audioSelectionState, new FakeAiPreflight());
            // No BookMutations: every mutation this file still covers goes through the legacy command
            // handler. The migrated families are proved on BookViewProjection, where a real write side
            // is the point.
            var projection = new BookViewProjection(
                loader, reader, reader, reader, mutations: null!, treeState, selectionState,
                audioSelectionState, coordinator, voiceResolver, new BookRevisionSequence());
            var presenter = new BookHierarchyPresenter(reader, projection, commandHandler, bookUseCases, selectionState, audioSelectionState, dialogService, snackbar, characterQueue, audioQueue, audioReviews, nodeStatus, events);
            return new Context(presenter, projection, reader, loader, commandHandler, bookUseCases, treeState, audioReviews, nodeStatus, voiceResolver, characterQueue, audioQueue, events, roster, seed, dialogService, snackbar);
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
        // SetItemCharacterAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetItemCharacterAsync_UnknownCharacterId_SetsCharacterNull()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                CharacterId = Guid.NewGuid(),
                Character = new Character { Id = Guid.NewGuid(), Name = "Bob" },
                Order = "a"
            };

            await ctx.Presenter.SetItemCharacterAsync(Folder, item, null);
            await ctx.CommandHandler.Received(1).ExecuteAsync(Arg.Any<SetItemCharacterCommand>());

            Assert.Null(item.Character);
            Assert.Null(item.CharacterId);
        }

        [Fact]
        public async Task SetItemCharacterAsync_KnownCharacterId_SetsCharacter()
        {
            var ctx = Create();
            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "Alice" };

            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(hasContent: true, characters: [character]));
            await ctx.Presenter.LoadAsync(Folder);

            var item = new ParagraphItem { Id = Guid.NewGuid(), Order = "a" };
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, charId);

            Assert.Equal(charId, item.CharacterId);
            Assert.Equal("Alice", item.Character?.Name);
        }

        [Fact]
        public async Task SetItemCharacterAsync_StaleCharacterList_RefreshesAndSetsCharacter()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            Assert.Empty(ctx.Presenter.Characters);

            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "NewChar" };

            ctx.Roster.Add(character);

            var item = new ParagraphItem { Id = Guid.NewGuid(), Order = "a" };
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, charId);

            Assert.Equal(charId, item.CharacterId);
            Assert.Equal("NewChar", item.Character?.Name);
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
        // SetItemCharacterAsync clears queue outcome
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetItemCharacterAsync_ClearsQueueOutcome()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var paragraphId = Guid.NewGuid();
            var queuedItem = new QueuedParagraph(Folder, paragraphId, "Preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            ctx.CharacterQueue.Enqueue([queuedItem]);
            ctx.CharacterQueue.MarkProcessing(queuedItem);
            ctx.CharacterQueue.Apply(queuedItem, new Disposition.Failed("some error"));
            Assert.NotNull(ctx.CharacterQueue.OutcomeOf(Folder, paragraphId));

            var item = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = paragraphId, Order = "a" };
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, null);

            Assert.Null(ctx.CharacterQueue.OutcomeOf(Folder, paragraphId));
        }

        // ---------------------------------------------------------------
        // DismissAudioReviewAsync — issues command + faints in-memory review
        // ---------------------------------------------------------------

        [Fact]
        public async Task DismissAudioReviewAsync_IssuesCommand_AndSetsInMemoryDismissed()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var itemId = Guid.NewGuid();
            ctx.AudioReviews.Set(Folder, itemId, new AudioReviewInfo(
                Read2Me.Core.Models.AudioReviewState.NeedsReview, NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.3, VerifyReason: "WER 0.3 > 0.15",
                Transcript: "t", OriginalTextSnapshot: "o"));

            await ctx.Presenter.DismissAudioReviewAsync(Folder, itemId);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<DismissAudioReviewCommand>(c => c != null && c.ParagraphItemId == itemId && c.FolderId.Value == Folder.Value));
            Assert.Equal(Read2Me.Core.Models.AudioReviewState.Dismissed, ctx.AudioReviews.ReviewOf(Folder, itemId)!.State);
        }

        // ---------------------------------------------------------------
        // DismissAudioReviewAsync — decrements review badge live (issue 0004)
        // ---------------------------------------------------------------

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

            // Seed an in-memory NeedsReview for the item.
            ctx.AudioReviews.Set(Folder, itemId, new AudioReviewInfo(
                Read2Me.Core.Models.AudioReviewState.NeedsReview,
                NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.3, VerifyReason: "WER too high",
                Transcript: "t", OriginalTextSnapshot: "o"));

            return (ctx, itemId, book);
        }

        [Fact]
        public async Task DismissAudioReviewAsync_DecrementsReviewBadge()
        {
            var (ctx, itemId, book) = await CreateWithReviewSeedRowAsync();
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).Review);

            await ctx.Presenter.DismissAudioReviewAsync(Folder, itemId);

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).Review);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.PartId).Review);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.VolumeId).Review);
        }

        // ---------------------------------------------------------------
        // NoteItemTextEditedAsync — a text edit returns the item to pre-generation state
        // ---------------------------------------------------------------

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
        // SetParagraphCharacterAsync — single command regardless of null/non-null id
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetParagraphCharacterAsync_Clearing_SendsSingleSetParagraphCharacterCommandWithNullId()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var paragraph = new Paragraph
            {
                Id = Guid.NewGuid(),
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "a", CharacterId = Guid.NewGuid() },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "b", CharacterId = Guid.NewGuid() },
                ]
            };

            await ctx.Presenter.SetParagraphCharacterAsync(Folder, paragraph, null);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphCharacterCommand>(c => c != null && c.ParagraphId == paragraph.Id && c.CharacterId == null));
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetItemCharacterCommand>());
        }

        // ---------------------------------------------------------------
        // ParagraphItemsChanged — presenter reloads that paragraph's items
        // ---------------------------------------------------------------
        private static async Task<(Context ctx, Paragraph para, OneChapterBook book)>
            CreateWithLoadedParagraphAsync()
        {
            var book = NewOneChapterBook();
            var para = new Paragraph
            {
                Id = Guid.NewGuid(),
                ChapterId = book.ChapterId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "a" },
                ]
            };

            var ctx = Create();
            await OpenWithChapterAsync(ctx, book, [para]);
            return (ctx, para, book);
        }

        [Fact]
        public async Task ParagraphItemsChanged_ReloadsThatParagraphsItems()
        {
            // Attribution re-segments: the paragraph's item list is replaced wholesale, not patched.
            var (ctx, para, book) = await CreateWithLoadedParagraphAsync();

            var charId = Guid.NewGuid();
            var reloaded = new Paragraph
            {
                Id = para.Id,
                ChapterId = book.ChapterId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "a", CharacterId = charId },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "b", CharacterId = ProjectDbContext.NarratorId },
                ]
            };
            ctx.Reader.GetChildrenAsync(Folder, BookNodeLevel.Chapter, book.ChapterId)
                .Returns(new HierarchyChildren(null, null, [reloaded]));

            ctx.Events.Publish(new ParagraphItemsChanged(Folder, para.Id));

            var published = ctx.Projection.Snapshot!.Branches.AllParagraphs().Single(p => p.Id == para.Id);
            Assert.Equal(2, published.Items.Count);
            Assert.Equal(charId, published.Items.First().CharacterId);
        }

        [Fact]
        public async Task ParagraphItemsChanged_ParagraphNotLoaded_DoesNotThrow()
        {
            var (ctx, _, _) = await CreateWithLoadedParagraphAsync();

            var ex = Record.Exception(() =>
                ctx.Events.Publish(new ParagraphItemsChanged(Folder, Guid.NewGuid())));

            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // Live attribution badge updates (issue 0002)
        // ---------------------------------------------------------------

        private static ParagraphStatusSeedRow MakeSeedRow(
            Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId, int unattributed) =>
            new(paragraphId, chapterId, partId, volumeId, unattributed, MissingAudio: 0, Review: 0);

        // ---------------------------------------------------------------
        // A flip reseeds derived counts and clears selection (ADR-0006)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetItemCharacterAsync_FlipIntoDialog_MakesTheParagraphSelectableAndCounted()
        {
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();
            // A chapter of pure narration: nothing attributable, so nothing selectable.
            var selectable = new HashSet<Guid>();
            var ctx = Create();
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(_ => EmptySnapshot(selectableNodes: selectable));
            await ctx.Presenter.LoadAsync(Folder);
            Assert.False(ctx.Presenter.IsNodeSelectable(ch));

            var para = new Paragraph { Id = paraId, ChapterId = ch };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Speech,
                CharacterId = ProjectDbContext.NarratorId,
                Paragraph = para,
            };
            para.Items.Add(item);

            // Giving it a character makes its paragraph a Character paragraph: the rebuild that ends
            // the write reads that back.
            selectable.UnionWith([ch, part, vol]);

            await ctx.Presenter.SetItemCharacterAsync(Folder, item, Guid.NewGuid());

            Assert.True(ctx.Presenter.IsNodeSelectable(ch));
            Assert.True(ctx.Presenter.IsNodeSelectable(part));
            Assert.True(ctx.Presenter.IsNodeSelectable(vol));
        }

        [Fact]
        public async Task SetItemCharacterAsync_FlipToNarrator_DropsTheParagraphOutOfTheCounts()
        {
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();
            var selectable = new HashSet<Guid> { ch, part, vol };
            var ctx = Create();
            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(_ => EmptySnapshot(
                    nodeCounts: new Dictionary<Guid, int> { [ch] = 1, [part] = 1, [vol] = 1 },
                    selectableNodes: selectable));
            await ctx.Presenter.LoadAsync(Folder);
            Assert.True(ctx.Presenter.IsNodeSelectable(ch));

            var para = new Paragraph { Id = paraId, ChapterId = ch };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Speech,
                CharacterId = Guid.NewGuid(),
                Paragraph = para,
            };
            para.Items.Add(item);

            // Its last dialog item becomes narration, so the paragraph stops being attributable.
            selectable.Clear();

            await ctx.Presenter.SetItemCharacterAsync(Folder, item, ProjectDbContext.NarratorId);

            Assert.False(ctx.Presenter.IsNodeSelectable(ch));
            Assert.False(ctx.Presenter.IsNodeSelectable(part));
            Assert.False(ctx.Presenter.IsNodeSelectable(vol));
        }

        [Fact]
        public async Task SetItemCharacterAsync_WhenTheCountsMove_SelectionIsCleared()
        {
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();
            var ctx = Create(nodeCounts: new Dictionary<Guid, int> { [ch] = 1, [part] = 1, [vol] = 1 });
            await ctx.Presenter.LoadAsync(Folder);
            ctx.Presenter.Selection.AddParagraph(paraId, new ParagraphSelection(vol, part, ch));
            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);

            var para = new Paragraph { Id = paraId, ChapterId = ch };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Speech,
                CharacterId = Guid.NewGuid(),
                Paragraph = para,
            };
            para.Items.Add(item);

            ctx.Reader.GetBookOverviewAsync(Folder).Returns(_ => new BookOverview(
                null, true, [], [], 0, 0, [], new Dictionary<Guid, int>()));

            await ctx.Presenter.SetItemCharacterAsync(Folder, item, ProjectDbContext.NarratorId);

            Assert.Equal(0, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task SetItemCharacterAsync_WhenTheCountsHold_SelectionSurvives()
        {
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();
            var ctx = Create(nodeCounts: new Dictionary<Guid, int> { [ch] = 1, [part] = 1, [vol] = 1 });
            await ctx.Presenter.LoadAsync(Folder);
            ctx.Presenter.Selection.AddParagraph(paraId, new ParagraphSelection(vol, part, ch));

            var para = new Paragraph { Id = paraId, ChapterId = ch };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Speech,
                CharacterId = Guid.NewGuid(),
                Paragraph = para,
            };
            para.Items.Add(item);

            // Swapping one character for another moves no denominator, so the dock bar stays up.
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, Guid.NewGuid());

            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task AssignCharacterToSelection_ReseedsOnceForTheWholeBatch()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 2));

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, Guid.NewGuid());

            // One reseed for the batch, not one per item — two items were stamped.
            await ctx.Reader.Received(1).GetBookOverviewAsync(Folder);
            Assert.NotEmpty(selected.Items);
        }

        [Fact]
        public async Task SetItemCharacterAsync_LastUnattributedItem_DecrementsChapterBadge()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            var seed = StubNodeStatus(ctx, MakeSeedRow(paraId, ch, part, vol, unattributed: 1));

            await ctx.Presenter.LoadAsync(Folder);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);

            var para = new Paragraph { Id = paraId };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Speech,
                Paragraph = para,
            };
            para.Items.Add(item);

            // Its last unattributed item gets a speaker, as the rebuild reads it back.
            seed[0] = MakeSeedRow(paraId, ch, part, vol, unattributed: 0);

            await ctx.Presenter.SetItemCharacterAsync(Folder, item, Guid.NewGuid());

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, vol).AttributionRemaining);
        }

        [Fact]
        public async Task SetItemCharacterAsync_NonLastUnattributedItem_DoesNotDecrementBadge()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            var seed = StubNodeStatus(ctx, MakeSeedRow(paraId, ch, part, vol, unattributed: 2));

            await ctx.Presenter.LoadAsync(Folder);

            var para = new Paragraph { Id = paraId };
            var item1 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Speech,
                Paragraph = para,
            };
            var item2 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "b",
                ItemType = ParagraphItemType.Speech,
                Paragraph = para,
            };
            para.Items.Add(item1);
            para.Items.Add(item2);

            // The write's effect, as the rebuild reads it back.
            seed[0] = MakeSeedRow(paraId, ch, part, vol, unattributed: 1);

            // Assign only item1; item2 remains unattributed
            await ctx.Presenter.SetItemCharacterAsync(Folder, item1, Guid.NewGuid());

            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);
        }

        [Fact]
        public async Task SetParagraphCharacterAsync_ZeroesEntireParagraphAttribution()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            var seed = StubNodeStatus(ctx, MakeSeedRow(paraId, ch, part, vol, unattributed: 3));

            await ctx.Presenter.LoadAsync(Folder);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);

            var para = new Paragraph
            {
                Id = paraId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "a" },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "b" },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "c" },
                ]
            };

            // Every item in the paragraph now has a speaker, as the rebuild reads it back.
            seed[0] = MakeSeedRow(paraId, ch, part, vol, unattributed: 0);

            await ctx.Presenter.SetParagraphCharacterAsync(Folder, para, Guid.NewGuid());

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, vol).AttributionRemaining);
        }

        [Fact]
        public async Task SetParagraphCharacterAsync_Clearing_ReportsEveryItemUnattributed()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            var seed = StubNodeStatus(ctx, MakeSeedRow(paraId, ch, part, vol, unattributed: 0));

            await ctx.Presenter.LoadAsync(Folder);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);

            var stamped = Guid.NewGuid();
            var para = new Paragraph
            {
                Id = paraId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "a", CharacterId = stamped },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "b", CharacterId = ProjectDbContext.NarratorId },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "c", CharacterId = stamped },
                ]
            };

            // The write's effect, as the rebuild reads it back.
            seed[0] = MakeSeedRow(paraId, ch, part, vol, unattributed: 2);

            await ctx.Presenter.SetParagraphCharacterAsync(Folder, para, null);

            // Clearing un-attributes both character items — the badge must rise, not report 0.
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, vol).AttributionRemaining);
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
        // Live audio badge updates (issue 0003)
        // ---------------------------------------------------------------

        private static async Task<(Context ctx, Paragraph para, OneChapterBook book)>
            CreateWithAudioSeedRowAsync(int missingAudio)
        {
            var book = NewOneChapterBook();
            var paraId = Guid.NewGuid();

            var para = new Paragraph
            {
                Id = paraId,
                ChapterId = book.ChapterId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = paraId, ItemType = ParagraphItemType.Speech, Order = "a" },
                ]
            };

            var ctx = Create();
            await OpenWithChapterAsync(ctx, book, [para], nodeStatusSeed:
            [
                new(paraId, book.ChapterId, book.PartId, book.VolumeId,
                    Unattributed: 0, MissingAudio: missingAudio, Review: 0),
            ]);

            return (ctx, para, book);
        }

        [Fact]
        public async Task OnAudioFileAssigned_LastMissingItem_DecrementsAudioBadge()
        {
            var (ctx, para, book) = await CreateWithAudioSeedRowAsync(missingAudio: 1);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, book.ChapterId, book.PartId, book.VolumeId);
            ctx.AudioQueue.Apply(new QueuedAudioItem(Folder, itemRef), new Disposition.Complete(null, "audio/item.wav"));

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.PartId).AudioRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.VolumeId).AudioRemaining);
        }

        [Fact]
        public async Task OnAudioFileAssigned_NonLastMissingItem_DoesNotDecrementToZero()
        {
            var (ctx, para, book) = await CreateWithAudioSeedRowAsync(missingAudio: 2);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, book.ChapterId, book.PartId, book.VolumeId);
            ctx.AudioQueue.Apply(new QueuedAudioItem(Folder, itemRef), new Disposition.Complete(null, "audio/item.wav"));

            // Still 1 missing audio item in paragraph → still contributes 1 to node count
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);
        }

        [Fact]
        public async Task SetItemCharacterAsync_ClearCharacter_RaisesBadge()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            var seed = StubNodeStatus(ctx, MakeSeedRow(paraId, ch, part, vol, unattributed: 0));

            await ctx.Presenter.LoadAsync(Folder);

            var para = new Paragraph { Id = paraId };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Speech,
                CharacterId = Guid.NewGuid(),
                Paragraph = para,
            };
            para.Items.Add(item);

            // A flip reseeds the badges from a rebuild rather than patching a counter (ADR-0006).
            seed[0] = MakeSeedRow(paraId, ch, part, vol, unattributed: 1);

            // Clear the character — item becomes unattributed, badge rises to 1
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, null);

            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);
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
        // OnAudioFileAssigned — item stamping and node status (issue 004)
        // ---------------------------------------------------------------

        [Fact]
        public async Task OnAudioFileAssigned_KnownItem_StampsAudioFileName()
        {
            var (ctx, para, book) = await CreateWithAudioSeedRowAsync(missingAudio: 1);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, book.ChapterId, book.PartId, book.VolumeId);
            ctx.AudioQueue.Apply(new QueuedAudioItem(Folder, itemRef), new Disposition.Complete(null, "audio/chapter1/item.wav"));

            Assert.Equal("audio/chapter1/item.wav", item.AudioFileName);
        }

        [Fact]
        public async Task OnAudioFileAssigned_KnownItem_NodeStatusDecrementsAudioBadge()
        {
            var (ctx, para, book) = await CreateWithAudioSeedRowAsync(missingAudio: 1);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, book.ChapterId, book.PartId, book.VolumeId);
            ctx.AudioQueue.Apply(new QueuedAudioItem(Folder, itemRef), new Disposition.Complete(null, "audio/item.wav"));

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, book.ChapterId).AudioRemaining);
        }

        [Fact]
        public async Task OnAudioFileAssigned_UnknownItemId_NoException()
        {
            var (ctx, para, book) = await CreateWithAudioSeedRowAsync(missingAudio: 1);

            var unknownRef = new AudioItemRef(Guid.NewGuid(), Guid.NewGuid(), book.ChapterId, book.PartId, book.VolumeId);
            var ex = Record.Exception(() => ctx.AudioQueue.Apply(
                new QueuedAudioItem(Folder, unknownRef), new Disposition.Complete(null, "audio/ghost.wav")));

            Assert.Null(ex);
        }

        [Fact]
        public async Task OnAudioFileAssigned_WrongFolder_ItemNotStamped()
        {
            var (ctx, para, book) = await CreateWithAudioSeedRowAsync(missingAudio: 1);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, book.ChapterId, book.PartId, book.VolumeId);
            var otherFolder = new ProjectFolderId("other-book");
            ctx.AudioQueue.Apply(new QueuedAudioItem(otherFolder, itemRef), new Disposition.Complete(null, "audio/item.wav"));

            Assert.Null(item.AudioFileName);
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

        // ---------------------------------------------------------------
        // AssignCharacterToSelectionAsync — the bulk apply path
        // ---------------------------------------------------------------

        /// <summary>
        /// Two paragraphs loaded under one expanded chapter, the first of them selected. The reader
        /// answers the preview with <paramref name="preview"/> and the confirm resolves to
        /// <paramref name="confirmed"/>.
        /// </summary>
        private static async Task<(Context ctx, Paragraph selected, Paragraph unselected)>
            CreateWithBulkSelectionAsync(BulkAssignPreview preview, bool confirmed = true)
        {
            var book = NewOneChapterBook();

            static Paragraph MakeParagraph(Guid chapter) =>
                new()
                {
                    Id = Guid.NewGuid(),
                    ChapterId = chapter,
                    Items =
                    [
                        new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "a" },
                        new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Order = "b", CharacterId = ProjectDbContext.NarratorId },
                    ],
                };

            var selected = MakeParagraph(book.ChapterId);
            var unselected = MakeParagraph(book.ChapterId);

            var ctx = Create();
            ctx.Reader.GetBulkAssignPreviewAsync(Folder, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(preview);

            StubConfirm(ctx.Dialogs, confirmed);

            await OpenWithChapterAsync(ctx, book, [selected, unselected]);

            ctx.Presenter.Selection.AddParagraph(
                selected.Id, new ParagraphSelection(book.VolumeId, book.PartId, book.ChapterId));

            return (ctx, selected, unselected);
        }

        private static void StubConfirm(IDialogService dialogs, bool confirmed)
        {
            var dialogRef = Substitute.For<IDialogReference>();
            dialogRef.Result.Returns(Task.FromResult<DialogResult?>(
                confirmed ? DialogResult.Ok(true) : DialogResult.Cancel()));

            dialogs.ShowAsync<Read2Me.App.Shared.ConfirmDialog>(
                    Arg.Any<string>(),
                    Arg.Any<DialogParameters<Read2Me.App.Shared.ConfirmDialog>>())
                .Returns(Task.FromResult(dialogRef));
        }

        private static (string Title, string Message, string ConfirmText) CapturedConfirm(IDialogService dialogs)
        {
            var call = dialogs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IDialogService.ShowAsync));
            var args = call.GetArguments();
            var parameters = (DialogParameters<Read2Me.App.Shared.ConfirmDialog>)args[1]!;
            return ((string)args[0]!, (string)parameters["Message"]!, (string)parameters["ConfirmText"]!);
        }

        [Fact]
        public async Task AssignCharacterToSelection_RunsOutcomeClearThenCommandThenSeedThenStampThenNotify()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            var charId = Guid.NewGuid();
            var log = new List<string>();

            // A stored outcome so ClearOutcome actually raises Changed.
            var queued = new QueuedParagraph(Folder, selected.Id, "preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            ctx.CharacterQueue.Enqueue([queued]);
            ctx.CharacterQueue.MarkProcessing(queued);
            ctx.CharacterQueue.Apply(queued, new Disposition.Failed("boom"));

            ctx.CommandHandler.ExecuteAsync(Arg.Any<SetParagraphsCharacterCommand>())
                .Returns(_ => { log.Add("command"); return (Guid?)null; });
            ctx.Reader.GetBookOverviewAsync(Folder)
                .Returns(_ =>
                {
                    log.Add("reseed");
                    return new BookOverview(null, true, [], [], 0, 0, [], new Dictionary<Guid, int>());
                });
            ctx.CharacterQueue.Changed += () => log.Add("outcome-cleared");
            ctx.Presenter.StateChanged += () =>
                log.Add(selected.Items.First().CharacterId == charId ? "notify-after-stamp" : "notify-before-stamp");

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, charId);

            // The unknown character id is resolved by a rebuild first — that publish is the leading
            // notify, and it lands before anything is written.
            Assert.Equal(new[] { "notify-before-stamp", "outcome-cleared", "command", "reseed", "notify-after-stamp" }, log);
        }

        [Fact]
        public async Task AssignCharacterToSelection_StampsSelectedLoadedParagraphsOnly()
        {
            var (ctx, selected, unselected) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            var charId = Guid.NewGuid();
            ctx.Roster.Add(new Character { Id = charId, Name = "Zelda" });

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, charId);

            Assert.Equal(charId, selected.Items.First().CharacterId);
            Assert.Equal("Zelda", selected.Items.First().Character?.Name);
            // Narration is never stamped, and a loaded paragraph outside the selection is untouched.
            Assert.Equal(ProjectDbContext.NarratorId, selected.Items.Last().CharacterId);
            Assert.Null(unselected.Items.First().CharacterId);
        }

        [Fact]
        public async Task AssignCharacterToSelection_IssuesOneBulkCommandCarryingTheSelectedIds()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            var charId = Guid.NewGuid();

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, charId);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphsCharacterCommand>(c =>
                    c != null && c.CharacterId == charId &&
                    c.ParagraphIds.Count == 1 && c.ParagraphIds[0] == selected.Id));
        }

        [Fact]
        public async Task AssignCharacterToSelection_NullId_ClearsAcrossTheSelection()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            selected.Items.First().CharacterId = Guid.NewGuid();

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, null);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphsCharacterCommand>(c => c != null && c.CharacterId == null));
            Assert.Null(selected.Items.First().CharacterId);

            var (title, message, confirmText) = CapturedConfirm(ctx.Dialogs);
            Assert.Equal("Clear speakers in selection", title);
            Assert.Equal("1 dialog line in 1 paragraph lose their speaker and need attributing again.", message);
            Assert.Equal("Clear", confirmText);
            ctx.Snackbar.Received(1).Add(
                "Cleared speakers on 1 lines in 1 paragraphs.", Severity.Success,
                Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
        }

        [Fact]
        public async Task AssignCharacterToSelection_NoDialogInSelection_InfoSnackbarNoConfirmNoCommand()
        {
            var (ctx, _, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(0, 0));

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, Guid.NewGuid());

            ctx.Snackbar.Received(1).Add(
                "No dialog in the selection — nothing to assign.", Severity.Info,
                Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
            Assert.Empty(ctx.Dialogs.ReceivedCalls());
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetParagraphsCharacterCommand>());
        }

        [Fact]
        public async Task AssignCharacterToSelection_CancelledConfirm_WritesNothing_AndKeepsSelectionAndBulkMode()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1), confirmed: false);
            await ctx.Presenter.SetBulkAssignAsync(true);

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, Guid.NewGuid());

            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetParagraphsCharacterCommand>());
            Assert.Null(selected.Items.First().CharacterId);
            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(selected.Id));
            Assert.True(ctx.Presenter.Selection.BulkMode);
        }

        [Fact]
        public async Task AssignCharacterToSelection_UnknownCharacterId_RefreshesRosterBeforeStamping()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            Assert.Empty(ctx.Presenter.Characters);

            var charId = Guid.NewGuid();
            ctx.Roster.Add(new Character { Id = charId, Name = "NewChar" });

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, charId);

            Assert.Equal("NewChar", selected.Items.First().Character?.Name);
            Assert.Equal("Assign NewChar to selection", CapturedConfirm(ctx.Dialogs).Title);
        }

        [Fact]
        public async Task AssignCharacterToSelection_IdTheRosterCannotExplain_StillReadsAsAnAssign()
        {
            // Defensive: the roster refresh above cannot place the id. The wording must not flip to
            // the clear verbs, because a character id is still being written.
            var (ctx, _, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, Guid.NewGuid());

            var (title, _, confirmText) = CapturedConfirm(ctx.Dialogs);
            Assert.Equal("Assign the character to selection", title);
            Assert.Equal("Assign", confirmText);
        }

        [Fact]
        public async Task AssignCharacterToSelection_KeepsTheSelection()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, Guid.NewGuid());

            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(selected.Id));
            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task AssignCharacterToSelection_ReSeedsNodeStatusForTheWholeFolder()
        {
            var (ctx, _, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid();

            ctx.NodeStatusSeed.Add(MakeSeedRow(Guid.NewGuid(), ch, part, vol, unattributed: 2));

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, Guid.NewGuid());

            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);
        }

        [Fact]
        public async Task AssignCharacterToSelection_ConfirmQuotesTheFigures_AndNamesTheSkippedParagraphs()
        {
            // 3 paragraphs selected, 2 of them holding the 5 dialog lines.
            var (ctx, _, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(2, 5));
            // Two more selected paragraphs under a chapter that was never expanded — not loaded, so
            // they only move the counts.
            var unloadedChapterId = Guid.NewGuid();
            ctx.Presenter.Selection.AddParagraph(Guid.NewGuid(), new ParagraphSelection(Guid.NewGuid(), Guid.NewGuid(), unloadedChapterId));
            ctx.Presenter.Selection.AddParagraph(Guid.NewGuid(), new ParagraphSelection(Guid.NewGuid(), Guid.NewGuid(), unloadedChapterId));

            var charId = Guid.NewGuid();
            ctx.Roster.Add(new Character { Id = charId, Name = "Zelda" });

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, charId);

            var (title, message, confirmText) = CapturedConfirm(ctx.Dialogs);
            Assert.Equal("Assign Zelda to selection", title);
            Assert.Equal(
                "Zelda becomes the speaker for 5 dialog lines in 2 paragraphs. Existing speakers are replaced. " +
                "1 selected paragraph have no dialog and stay unchanged.",
                message);
            Assert.Equal("Assign", confirmText);
            ctx.Snackbar.Received(1).Add(
                "Assigned Zelda to 5 lines in 2 paragraphs.", Severity.Success,
                Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
        }

        [Fact]
        public async Task AssignCharacterToSelection_NoSkippedParagraphs_OmitsThatSentence()
        {
            var (ctx, _, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 4));
            var charId = Guid.NewGuid();
            ctx.Roster.Add(new Character { Id = charId, Name = "Zelda" });

            await ctx.Presenter.AssignCharacterToSelectionAsync(Folder, charId);

            Assert.Equal(
                "Zelda becomes the speaker for 4 dialog lines in 1 paragraph. Existing speakers are replaced.",
                CapturedConfirm(ctx.Dialogs).Message);
        }

        // ---------------------------------------------------------------
        // AssignCharacterAsync — the chip front door
        // ---------------------------------------------------------------

        [Fact]
        public async Task AssignCharacter_ArmedAndInSelection_ParagraphChip_FansOutAcrossTheSelection()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            await ctx.Presenter.SetBulkAssignAsync(true);
            var charId = Guid.NewGuid();

            await ctx.Presenter.AssignCharacterAsync(Folder, selected, null, charId);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphsCharacterCommand>(c => c != null && c.CharacterId == charId));
            await AssertNoSingleAssignAsync(ctx);
        }

        [Fact]
        public async Task AssignCharacter_ArmedAndInSelection_SegmentChip_FansOutTheSameWay()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            await ctx.Presenter.SetBulkAssignAsync(true);
            var charId = Guid.NewGuid();

            await ctx.Presenter.AssignCharacterAsync(Folder, selected, selected.Items.First(), charId);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphsCharacterCommand>(c => c != null && c.CharacterId == charId));
            await AssertNoSingleAssignAsync(ctx);
        }

        [Fact]
        public async Task AssignCharacter_ArmedAndInSelection_NullId_FansOutAsAClear()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            await ctx.Presenter.SetBulkAssignAsync(true);

            await ctx.Presenter.AssignCharacterAsync(Folder, selected, null, null);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphsCharacterCommand>(c => c != null && c.CharacterId == null));
            await AssertNoSingleAssignAsync(ctx);
        }

        /// <summary>Neither single-assign leg fired — the pick went out as one bulk command only.</summary>
        private static async Task AssertNoSingleAssignAsync(Context ctx)
        {
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetParagraphCharacterCommand>());
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetItemCharacterCommand>());
        }

        [Fact]
        public async Task AssignCharacter_ArmedButRowOutsideTheSelection_AssignsSingly()
        {
            var (ctx, _, unselected) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            await ctx.Presenter.SetBulkAssignAsync(true);
            var charId = Guid.NewGuid();

            await ctx.Presenter.AssignCharacterAsync(Folder, unselected, null, charId);
            await ctx.Presenter.AssignCharacterAsync(Folder, unselected, unselected.Items.First(), charId);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphCharacterCommand>(c =>
                    c != null && c.ParagraphId == unselected.Id && c.CharacterId == charId));
            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetItemCharacterCommand>(c =>
                    c != null && c.ItemId == unselected.Items.First().Id && c.CharacterId == charId));
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetParagraphsCharacterCommand>());
        }

        [Fact]
        public async Task AssignCharacter_Disarmed_AssignsSingly_BothChipKinds()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));
            var charId = Guid.NewGuid();

            Assert.False(ctx.Presenter.Selection.BulkMode);

            await ctx.Presenter.AssignCharacterAsync(Folder, selected, null, charId);
            await ctx.Presenter.AssignCharacterAsync(Folder, selected, selected.Items.First(), charId);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphCharacterCommand>(c =>
                    c != null && c.ParagraphId == selected.Id && c.CharacterId == charId));
            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetItemCharacterCommand>(c =>
                    c != null && c.ItemId == selected.Items.First().Id && c.CharacterId == charId));
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetParagraphsCharacterCommand>());
        }

        [Fact]
        public async Task AssignCharacter_Disarmed_NullId_ClearsThatRowOnly()
        {
            var (ctx, selected, _) = await CreateWithBulkSelectionAsync(new BulkAssignPreview(1, 1));

            await ctx.Presenter.AssignCharacterAsync(Folder, selected, null, null);

            await ctx.CommandHandler.Received(1).ExecuteAsync(
                Arg.Is<SetParagraphCharacterCommand>(c => c != null && c.CharacterId == null));
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetParagraphsCharacterCommand>());
        }
    }
}
