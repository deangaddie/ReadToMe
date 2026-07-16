using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.NodeStatus;
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
            IProjectReader Reader,
            IBookProjectLoader Loader,
            IBookCommandHandler CommandHandler,
            FakeBookUseCases BookUseCases,
            BookTreeState TreeState,
            AudioReviewService AudioReviews,
            NodeStatusService NodeStatus,
            FakeVoiceResolver VoiceResolver);

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

        private static Context Create(IReadOnlyDictionary<Guid, int>? nodeCounts = null)
        {
            var reader = Substitute.For<IProjectReader>();
            var loader = Substitute.For<IBookProjectLoader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            loader.LoadSnapshotAsync(Arg.Any<ProjectFolderId>(), Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeCounts));

            reader.GetCharacterParagraphsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<BookNodeLevel>(), Arg.Any<Guid>(), Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>());

            reader.GetAudioItemRefsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<BookNodeLevel>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new List<AudioItemRef>());

            reader.GetCharactersAsync(Arg.Any<ProjectFolderId>())
                .Returns(new List<Character>());

            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var characterQueue = new CharacterQueueService();
            var snackbar = Substitute.For<ISnackbar>();
            var paragraphTtsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            paragraphTtsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);
            var audioReviews = new AudioReviewService();
            var nodeStatus = new NodeStatusService();
            var voiceResolver = new FakeVoiceResolver();
            var audioQueue = new AudioQueueService();
            var coordinator = new BookSelectionCoordinator(reader, characterQueue, audioQueue, paragraphTtsSettings, snackbar, selectionState, audioSelectionState, new FakeAiPreflight());
            var presenter = new BookHierarchyPresenter(reader, loader, commandHandler, bookUseCases, treeState, selectionState, audioSelectionState, dialogService, characterQueue, audioQueue, audioReviews, nodeStatus, voiceResolver, coordinator, new EventBroadcaster<ParagraphItemsChanged>());
            return new Context(presenter, reader, loader, commandHandler, bookUseCases, treeState, audioReviews, nodeStatus, voiceResolver);
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

        // ---------------------------------------------------------------
        // SplitAndReloadAsync — new panel expansion
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitAndReload_SourceExpanded_ExpandsNewParent()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var sourcePartId = Guid.NewGuid();
            var newPartId = Guid.NewGuid();
            ctx.Presenter.Tree.ExpandedPartIds.Add(sourcePartId);
            ctx.CommandHandler.ExecuteAsync(Arg.Any<SplitAtChapterCommand>()).Returns(newPartId);

            await ctx.Presenter.SplitAndReloadAsync(
                Folder,
                new SplitAtChapterCommand(Folder, Guid.NewGuid(), null),
                BookHierarchyPresenter.SplitLevel.Part,
                sourcePartId);

            Assert.Contains(sourcePartId, ctx.Presenter.Tree.ExpandedPartIds);
            Assert.Contains(newPartId, ctx.Presenter.Tree.ExpandedPartIds);
        }

        [Fact]
        public async Task SplitAndReload_SourceCollapsed_DoesNotExpandNewParent()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var sourcePartId = Guid.NewGuid();
            var newPartId = Guid.NewGuid();
            ctx.CommandHandler.ExecuteAsync(Arg.Any<SplitAtChapterCommand>()).Returns(newPartId);

            await ctx.Presenter.SplitAndReloadAsync(
                Folder,
                new SplitAtChapterCommand(Folder, Guid.NewGuid(), null),
                BookHierarchyPresenter.SplitLevel.Part,
                sourcePartId);

            Assert.DoesNotContain(newPartId, ctx.Presenter.Tree.ExpandedPartIds);
        }

        [Fact]
        public async Task SetItemCharacterAsync_StaleCharacterList_RefreshesAndSetsCharacter()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            Assert.Empty(ctx.Presenter.Characters);

            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "NewChar" };

            ctx.Reader.GetCharactersAsync(Folder).Returns(new List<Character> { character });

            var item = new ParagraphItem { Id = Guid.NewGuid(), Order = "a" };
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, charId);

            Assert.Equal(charId, item.CharacterId);
            Assert.Equal("NewChar", item.Character?.Name);
            await ctx.Reader.Received().GetCharactersAsync(Folder);
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
            await ctx.Presenter.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: true);

            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(pId));
        }

        [Fact]
        public async Task ToggleParagraphAsync_Off_RemovesFromSelection()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var pId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            await ctx.Presenter.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: true);
            await ctx.Presenter.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: false);

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
            await ctx.Presenter.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: true);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: false);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Volume, volId, on: true);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: true);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: false);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Volume, volId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Volume, volId, on: false);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: false);

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

            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: false);
            await ctx.Presenter.SetNodeAsync(Folder, BookNodeLevel.Part, ptId, on: true);

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
            var reader = ctx.Reader;
            var loader = ctx.Loader;
            var commandHandler = ctx.CommandHandler;
            var dialogService = Substitute.For<IDialogService>();
            var queue = new CharacterQueueService();
            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var snackbar = Substitute.For<ISnackbar>();
            var paragraphTtsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            paragraphTtsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);
            var audioQueueLocal = new AudioQueueService();
            var coordinatorLocal = new BookSelectionCoordinator(reader, queue, audioQueueLocal, paragraphTtsSettings, snackbar, selectionState, audioSelectionState, new FakeAiPreflight());
            var presenter = new BookHierarchyPresenter(reader, loader, commandHandler, new FakeBookUseCases(),
                treeState, selectionState, audioSelectionState, dialogService, queue, audioQueueLocal, new AudioReviewService(), new NodeStatusService(), new FakeVoiceResolver(), coordinatorLocal, new EventBroadcaster<ParagraphItemsChanged>());
            await presenter.LoadAsync(Folder);

            var paragraphId = Guid.NewGuid();
            var queuedItem = new QueuedParagraph(Folder, paragraphId, "Preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            queue.Enqueue([queuedItem]);
            queue.MarkProcessing(queuedItem);
            queue.MarkFailed(queuedItem, "some error");
            Assert.NotNull(queue.OutcomeOf(Folder, paragraphId));

            var item = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = paragraphId, Order = "a" };
            await presenter.SetItemCharacterAsync(Folder, item, null);

            Assert.Null(queue.OutcomeOf(Folder, paragraphId));
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

        private static ParagraphStatusSeedRow MakeReviewSeedRow(
            Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId, int review) =>
            new(paragraphId, chapterId, partId, volumeId, Unattributed: 0, MissingAudio: 0, Review: review);

        private static async Task<(Context ctx, Guid itemId, Guid chapterId, Guid partId, Guid volumeId)>
            CreateWithReviewSeedRow()
        {
            var volumeId = Guid.NewGuid(); var partId = Guid.NewGuid(); var chapterId = Guid.NewGuid();
            var paraId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var para = new Paragraph
            {
                Id = paraId,
                Items =
                [
                    new ParagraphItem { Id = itemId, ParagraphId = paraId, ItemType = ParagraphItemType.Character, Order = "a" },
                ]
            };

            var reader = Substitute.For<IProjectReader>();
            var loader = Substitute.For<IBookProjectLoader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            var seedRows = new List<ParagraphStatusSeedRow>
            {
                new(paraId, chapterId, partId, volumeId, Unattributed: 0, MissingAudio: 0, Review: 1),
            };
            loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(new BookProjectSnapshot(
                    Filename: null, HasContent: true, Volumes: [], Characters: [],
                    TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                    NodeCharacterParagraphCounts: new Dictionary<Guid, int>(),
                    NarratorOnlyMode: false,
                    AudioNodeCounts: new Dictionary<Guid, int>(),
                    AudioReviews: [],
                    NodeStatusSeed: seedRows));

            reader.GetChildrenAsync(Folder, BookNodeLevel.Chapter, chapterId)
                .Returns(new HierarchyChildren(null, null, new List<Paragraph> { para }));

            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var characterQueue = new CharacterQueueService();
            var audioQueue = new AudioQueueService();
            var snackbar = Substitute.For<ISnackbar>();
            var paragraphTtsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            paragraphTtsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);
            var audioReviews = new AudioReviewService();
            var nodeStatus = new NodeStatusService();
            var coordinator788 = new BookSelectionCoordinator(reader, characterQueue, audioQueue, paragraphTtsSettings, snackbar, selectionState, audioSelectionState, new FakeAiPreflight());
            var presenter = new BookHierarchyPresenter(reader, loader, commandHandler, bookUseCases, treeState, selectionState, audioSelectionState, dialogService, characterQueue, audioQueue, audioReviews, nodeStatus, new FakeVoiceResolver(), coordinator788, new EventBroadcaster<ParagraphItemsChanged>());

            await presenter.LoadAsync(Folder);
            // Expand chapter so paragraph is loaded into cache (item→paragraph mapping).
            await presenter.Tree.OnChapterExpandedAsync(new Chapter { Id = chapterId, Order = "a" }, expanded: true);

            // Seed an in-memory NeedsReview for the item.
            audioReviews.Set(Folder, itemId, new AudioReviewInfo(
                Read2Me.Core.Models.AudioReviewState.NeedsReview,
                NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.3, VerifyReason: "WER too high",
                Transcript: "t", OriginalTextSnapshot: "o"));

            var ctx = new Context(presenter, reader, loader, commandHandler, bookUseCases, treeState, audioReviews, nodeStatus, new FakeVoiceResolver());
            return (ctx, itemId, chapterId, partId, volumeId);
        }

        [Fact]
        public async Task DismissAudioReviewAsync_DecrementsReviewBadge()
        {
            var (ctx, itemId, chapterId, partId, volumeId) = await CreateWithReviewSeedRow();
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, chapterId).Review);

            await ctx.Presenter.DismissAudioReviewAsync(Folder, itemId);

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, chapterId).Review);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, partId).Review);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, volumeId).Review);
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
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "a", CharacterId = Guid.NewGuid() },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "b", CharacterId = Guid.NewGuid() },
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

        private static async Task<(Context ctx, CharacterQueueService queue, Paragraph para,
            QueuedParagraph queued, EventBroadcaster<ParagraphItemsChanged> events, IProjectReader reader)>
            CreateWithLoadedParagraph()
        {
            var chapterId = Guid.NewGuid();
            var para = new Paragraph
            {
                Id = Guid.NewGuid(),
                ChapterId = chapterId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "a" },
                ]
            };

            var reader = Substitute.For<IProjectReader>();
            var loader = Substitute.For<IBookProjectLoader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(new BookProjectSnapshot(
                    Filename: null, HasContent: true, Volumes: [], Characters: [],
                    TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                    NodeCharacterParagraphCounts: new Dictionary<Guid, int>(),
                    NarratorOnlyMode: false,
                    AudioNodeCounts: new Dictionary<Guid, int>(),
                    AudioReviews: [],
                    NodeStatusSeed: []));

            reader.GetChildrenAsync(Folder, BookNodeLevel.Chapter, chapterId)
                .Returns(new HierarchyChildren(null, null, new List<Paragraph> { para }));

            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var queue = new CharacterQueueService();
            var snackbar2 = Substitute.For<ISnackbar>();
            var paragraphTtsSettings2 = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            paragraphTtsSettings2.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);
            var audioQueue889 = new AudioQueueService();
            var coordinator889 = new BookSelectionCoordinator(reader, queue, audioQueue889, paragraphTtsSettings2, snackbar2, selectionState, audioSelectionState, new FakeAiPreflight());
            var events889 = new EventBroadcaster<ParagraphItemsChanged>();
            var presenter = new BookHierarchyPresenter(reader, loader, commandHandler, bookUseCases, treeState, selectionState, audioSelectionState, dialogService, queue, audioQueue889, new AudioReviewService(), new NodeStatusService(), new FakeVoiceResolver(), coordinator889, events889);

            await presenter.LoadAsync(Folder);
            // Expand chapter so paragraphs are loaded into the cache.
            await presenter.Tree.OnChapterExpandedAsync(new Chapter { Id = chapterId, Order = "a" }, expanded: true);

            var ctx = new Context(presenter, reader, loader, commandHandler, bookUseCases, treeState, new AudioReviewService(), new NodeStatusService(), new FakeVoiceResolver());
            var queuedPara = new QueuedParagraph(Folder, para.Id, "preview", chapterId, Guid.NewGuid(), Guid.NewGuid());
            return (ctx, queue, para, queuedPara, events889, reader);
        }

        [Fact]
        public async Task ParagraphItemsChanged_ReloadsThatParagraphsItems()
        {
            // Attribution re-segments: the paragraph's item list is replaced wholesale, not patched.
            var (_, _, para, queued, events, reader) = await CreateWithLoadedParagraph();

            var charId = Guid.NewGuid();
            var reloaded = new Paragraph
            {
                Id = para.Id,
                ChapterId = queued.ChapterId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "a", CharacterId = charId },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Narration, Order = "b" },
                ]
            };
            reader.GetChildrenAsync(Folder, BookNodeLevel.Chapter, queued.ChapterId)
                .Returns(new HierarchyChildren(null, null, [reloaded]));

            events.Publish(new ParagraphItemsChanged(Folder, para.Id));

            Assert.Equal(2, para.Items.Count);
            Assert.Equal(charId, para.Items.First().CharacterId);
        }

        [Fact]
        public async Task ParagraphItemsChanged_ParagraphNotInTree_DoesNotThrow()
        {
            var (_, _, _, _, events, _) = await CreateWithLoadedParagraph();

            var ex = Record.Exception(() =>
                events.Publish(new ParagraphItemsChanged(Folder, Guid.NewGuid())));

            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // Live attribution badge updates (issue 0002)
        // ---------------------------------------------------------------

        private static ParagraphStatusSeedRow MakeSeedRow(
            Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId, int unattributed) =>
            new(paragraphId, chapterId, partId, volumeId, unattributed, MissingAudio: 0, Review: 0);

        [Fact]
        public async Task SetItemCharacterAsync_LastUnattributedItem_DecrementsChapterBadge()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeStatusSeed: [MakeSeedRow(paraId, ch, part, vol, unattributed: 1)]));

            await ctx.Presenter.LoadAsync(Folder);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);

            var para = new Paragraph { Id = paraId };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Character,
                Paragraph = para,
            };
            para.Items.Add(item);

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

            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeStatusSeed: [MakeSeedRow(paraId, ch, part, vol, unattributed: 2)]));

            await ctx.Presenter.LoadAsync(Folder);

            var para = new Paragraph { Id = paraId };
            var item1 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Character,
                Paragraph = para,
            };
            var item2 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "b",
                ItemType = ParagraphItemType.Character,
                Paragraph = para,
            };
            para.Items.Add(item1);
            para.Items.Add(item2);

            // Assign only item1; item2 remains unattributed
            await ctx.Presenter.SetItemCharacterAsync(Folder, item1, Guid.NewGuid());

            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);
        }

        [Fact]
        public async Task SetParagraphCharacterAsync_ZeroesEntireParagraphAttribution()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeStatusSeed: [MakeSeedRow(paraId, ch, part, vol, unattributed: 3)]));

            await ctx.Presenter.LoadAsync(Folder);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);

            var para = new Paragraph
            {
                Id = paraId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "a" },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "b" },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "c" },
                ]
            };

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

            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeStatusSeed: [MakeSeedRow(paraId, ch, part, vol, unattributed: 0)]));

            await ctx.Presenter.LoadAsync(Folder);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, ch).AttributionRemaining);

            var stamped = Guid.NewGuid();
            var para = new Paragraph
            {
                Id = paraId,
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "a", CharacterId = stamped },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Narration, Order = "b" },
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "c", CharacterId = stamped },
                ]
            };

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

            await ctx.Presenter.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true, needsAudioOnly: true);

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

            await ctx.Presenter.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);

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

            await ctx.Presenter.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true, needsAudioOnly: true);

            Assert.Equal(TriState.Indeterminate, ctx.Presenter.AudioSelection.NodeState(BookNodeLevel.Chapter, chId));
        }

        // ---------------------------------------------------------------
        // Live audio badge updates (issue 0003)
        // ---------------------------------------------------------------

        private static ParagraphStatusSeedRow MakeAudioSeedRow(
            Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId, int missingAudio) =>
            new(paragraphId, chapterId, partId, volumeId, Unattributed: 0, MissingAudio: missingAudio, Review: 0);

        private static async Task<(Context ctx, AudioQueueService audioQueue, Paragraph para, Guid chapterId, Guid partId, Guid volumeId)>
            CreateWithAudioSeedRow(int missingAudio)
        {
            var volumeId = Guid.NewGuid(); var partId = Guid.NewGuid(); var chapterId = Guid.NewGuid();
            var paraId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var para = new Paragraph
            {
                Id = paraId,
                Items =
                [
                    new ParagraphItem { Id = itemId, ParagraphId = paraId, ItemType = ParagraphItemType.Character, Order = "a" },
                ]
            };

            var reader = Substitute.For<IProjectReader>();
            var loader = Substitute.For<IBookProjectLoader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            var seedRows = new List<ParagraphStatusSeedRow>
            {
                new(paraId, chapterId, partId, volumeId, Unattributed: 0, MissingAudio: missingAudio, Review: 0),
            };
            loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(new BookProjectSnapshot(
                    Filename: null, HasContent: true, Volumes: [], Characters: [],
                    TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                    NodeCharacterParagraphCounts: new Dictionary<Guid, int>(),
                    NarratorOnlyMode: false,
                    AudioNodeCounts: new Dictionary<Guid, int>(),
                    AudioReviews: [],
                    NodeStatusSeed: seedRows));

            reader.GetChildrenAsync(Folder, BookNodeLevel.Chapter, chapterId)
                .Returns(new HierarchyChildren(null, null, new List<Paragraph> { para }));

            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var characterQueue = new CharacterQueueService();
            var audioQueue = new AudioQueueService();
            var snackbar = Substitute.For<ISnackbar>();
            var paragraphTtsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            paragraphTtsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);
            var audioReviews = new AudioReviewService();
            var nodeStatus = new NodeStatusService();
            var coordinator1173 = new BookSelectionCoordinator(reader, characterQueue, audioQueue, paragraphTtsSettings, snackbar, selectionState, audioSelectionState, new FakeAiPreflight());
            var presenter = new BookHierarchyPresenter(reader, loader, commandHandler, bookUseCases, treeState, selectionState, audioSelectionState, dialogService, characterQueue, audioQueue, audioReviews, nodeStatus, new FakeVoiceResolver(), coordinator1173, new EventBroadcaster<ParagraphItemsChanged>());

            await presenter.LoadAsync(Folder);
            await presenter.Tree.OnChapterExpandedAsync(new Chapter { Id = chapterId, Order = "a" }, expanded: true);

            var ctx = new Context(presenter, reader, loader, commandHandler, bookUseCases, treeState, audioReviews, nodeStatus, new FakeVoiceResolver());
            return (ctx, audioQueue, para, chapterId, partId, volumeId);
        }

        [Fact]
        public async Task OnAudioFileAssigned_LastMissingItem_DecrementsAudioBadge()
        {
            var (ctx, audioQueue, para, chapterId, partId, volumeId) = await CreateWithAudioSeedRow(missingAudio: 1);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, chapterId).AudioRemaining);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, chapterId, partId, volumeId);
            audioQueue.MarkComplete(Folder, itemRef, "audio/item.wav");

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, chapterId).AudioRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, partId).AudioRemaining);
            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, volumeId).AudioRemaining);
        }

        [Fact]
        public async Task OnAudioFileAssigned_NonLastMissingItem_DoesNotDecrementToZero()
        {
            var (ctx, audioQueue, para, chapterId, partId, volumeId) = await CreateWithAudioSeedRow(missingAudio: 2);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, chapterId).AudioRemaining);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, chapterId, partId, volumeId);
            audioQueue.MarkComplete(Folder, itemRef, "audio/item.wav");

            // Still 1 missing audio item in paragraph → still contributes 1 to node count
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, chapterId).AudioRemaining);
        }

        [Fact]
        public async Task SetItemCharacterAsync_ClearCharacter_RaisesBadge()
        {
            var ctx = Create();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var paraId = Guid.NewGuid();

            ctx.Loader.LoadSnapshotAsync(Folder, Arg.Any<CancellationToken>())
                .Returns(EmptySnapshot(nodeStatusSeed: [MakeSeedRow(paraId, ch, part, vol, unattributed: 0)]));

            await ctx.Presenter.LoadAsync(Folder);

            var para = new Paragraph { Id = paraId };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = paraId, Order = "a",
                ItemType = ParagraphItemType.Character,
                CharacterId = Guid.NewGuid(),
                Paragraph = para,
            };
            para.Items.Add(item);

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
            await ctx.Presenter.ToggleParagraphAsync(Folder, Guid.NewGuid(), chId, ptId, volId, on: true);
            Assert.Equal(1, ctx.Presenter.SelectedParagraphCount);

            ctx.Presenter.ViewMode = BookViewMode.SplitAudio;

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

            ctx.Presenter.ViewMode = BookViewMode.SplitAudio;

            Assert.Equal(0, ctx.Presenter.SelectedAudioItemCount);
        }

        [Fact]
        public async Task SetViewMode_NewValue_FiresStateChanged()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            int fired = 0;
            ctx.Presenter.StateChanged += () => fired++;

            ctx.Presenter.ViewMode = BookViewMode.SplitAudio;

            Assert.Equal(1, fired);
        }

        [Fact]
        public async Task SetViewMode_SameValue_DoesNotFireStateChanged()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            int fired = 0;
            ctx.Presenter.StateChanged += () => fired++;

            ctx.Presenter.ViewMode = BookViewMode.Combined; // already Combined

            Assert.Equal(0, fired);
        }

        // ---------------------------------------------------------------
        // EnsureVoicePreviewAsync — delegates to IVoiceResolver (001c)
        // ---------------------------------------------------------------

        [Fact]
        public async Task EnsureVoicePreviewAsync_ReturnsNamesFromVoiceResolver()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var itemId = Guid.NewGuid();
            ctx.VoiceResolver.SetName(itemId, "Alice Voice");

            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            Assert.Equal("Alice Voice", ctx.Presenter.ResolvedVoiceName(itemId));
        }

        [Fact]
        public async Task EnsureVoicePreviewAsync_NullName_CachedAsNull()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var itemId = Guid.NewGuid();
            ctx.VoiceResolver.SetName(itemId, null);

            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            Assert.Null(ctx.Presenter.ResolvedVoiceName(itemId));
        }

        [Fact]
        public async Task EnsureVoicePreviewAsync_SecondCall_SameId_DoesNotReResolve()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var itemId = Guid.NewGuid();
            ctx.VoiceResolver.SetName(itemId, "First");
            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            // Cached: second call must NOT re-resolve a name already known.
            ctx.VoiceResolver.SetName(itemId, "Changed");
            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            Assert.Equal("First", ctx.Presenter.ResolvedVoiceName(itemId));
        }

        [Fact]
        public async Task EnsureVoicePreviewAsync_AfterInvalidate_ReResolves()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var itemId = Guid.NewGuid();
            ctx.VoiceResolver.SetName(itemId, "First");
            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            ctx.VoiceResolver.SetName(itemId, "Changed");
            ctx.Presenter.InvalidateVoicePreview();
            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            Assert.Equal("Changed", ctx.Presenter.ResolvedVoiceName(itemId));
        }

        [Fact]
        public async Task EnteringSplitAudio_InvalidatesVoicePreview()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var itemId = Guid.NewGuid();
            ctx.VoiceResolver.SetName(itemId, "First");
            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            // Simulate a voice-rule edit on another tab, then re-enter SplitAudio.
            ctx.VoiceResolver.SetName(itemId, "Changed");
            ctx.Presenter.ViewMode = BookViewMode.SplitAudio;
            await ctx.Presenter.EnsureVoicePreviewAsync(Folder, [itemId]);

            Assert.Equal("Changed", ctx.Presenter.ResolvedVoiceName(itemId));
        }

        // ---------------------------------------------------------------
        // OnAudioFileAssigned — item stamping and node status (issue 004)
        // ---------------------------------------------------------------

        [Fact]
        public async Task OnAudioFileAssigned_KnownItem_StampsAudioFileName()
        {
            var (ctx, audioQueue, para, chapterId, partId, volumeId) = await CreateWithAudioSeedRow(missingAudio: 1);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, chapterId, partId, volumeId);
            audioQueue.MarkComplete(Folder, itemRef, "audio/chapter1/item.wav");

            Assert.Equal("audio/chapter1/item.wav", item.AudioFileName);
        }

        [Fact]
        public async Task OnAudioFileAssigned_KnownItem_NodeStatusDecrementsAudioBadge()
        {
            var (ctx, audioQueue, para, chapterId, partId, volumeId) = await CreateWithAudioSeedRow(missingAudio: 1);
            Assert.Equal(1, ctx.NodeStatus.StatusForNode(Folder, chapterId).AudioRemaining);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, chapterId, partId, volumeId);
            audioQueue.MarkComplete(Folder, itemRef, "audio/item.wav");

            Assert.Equal(0, ctx.NodeStatus.StatusForNode(Folder, chapterId).AudioRemaining);
        }

        [Fact]
        public async Task OnAudioFileAssigned_UnknownItemId_NoException()
        {
            var (ctx, audioQueue, para, chapterId, partId, volumeId) = await CreateWithAudioSeedRow(missingAudio: 1);

            var unknownRef = new AudioItemRef(Guid.NewGuid(), Guid.NewGuid(), chapterId, partId, volumeId);
            var ex = Record.Exception(() => audioQueue.MarkComplete(Folder, unknownRef, "audio/ghost.wav"));

            Assert.Null(ex);
        }

        [Fact]
        public async Task OnAudioFileAssigned_WrongFolder_ItemNotStamped()
        {
            var (ctx, audioQueue, para, chapterId, partId, volumeId) = await CreateWithAudioSeedRow(missingAudio: 1);

            var item = para.Items.First();
            var itemRef = new AudioItemRef(item.Id, para.Id, chapterId, partId, volumeId);
            var otherFolder = new ProjectFolderId("other-book");
            audioQueue.MarkComplete(otherFolder, itemRef, "audio/item.wav");

            Assert.Null(item.AudioFileName);
        }
    }
}
