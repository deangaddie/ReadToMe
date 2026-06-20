using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.UseCases;
using Xunit;
using Read2Me.AppData.Entities;

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
            IBookCommandHandler CommandHandler,
            FakeBookUseCases BookUseCases,
            BookTreeState TreeState);

        private static Context Create(IReadOnlyDictionary<Guid, int>? nodeCounts = null)
        {
            var reader = Substitute.For<IProjectReader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: false, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                NodeCharacterParagraphCounts: nodeCounts ?? new Dictionary<Guid, int>()));

            // Default: single method returns empty
            reader.GetCharacterParagraphsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<BookNodeLevel>(), Arg.Any<Guid>(), Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>());

            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();
            var characterQueue = new CharacterQueueService();
            var snackbar = Substitute.For<ISnackbar>();
            var paragraphTtsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            paragraphTtsSettings.GetActiveConfigAsync().Returns((Read2Me.AppData.Entities.ParagraphTtsServiceConfig?)null);
            var presenter = new BookHierarchyPresenter(reader, commandHandler, bookUseCases, treeState, selectionState, audioSelectionState, dialogService, snackbar, paragraphTtsSettings, characterQueue, new AudioQueueService());
            return new Context(presenter, reader, commandHandler, bookUseCases, treeState);
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
            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: true,
                Volumes: new List<Volume> { vol }, Characters: [],
                TotalParts: 1, TotalChapters: 1, SelectableNodeIds: [], NodeCharacterParagraphCounts: new Dictionary<Guid, int>()));
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

            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: true, Volumes: [], Characters: [character],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [], NodeCharacterParagraphCounts: new Dictionary<Guid, int>()));
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

            ctx.Reader.GetBookOverviewAsync(other).Returns(new BookOverview(
                Filename: null, HasContent: false, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [], NodeCharacterParagraphCounts: new Dictionary<Guid, int>()));

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
            // Seed counts so NodeState can derive Checked
            var counts = new Dictionary<Guid, int> { [chId] = 1 };
            var ctx = Create(counts);
            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: false, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                NodeCharacterParagraphCounts: counts));
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

            // Seed counts so derived NodeState returns Checked
            var counts = new Dictionary<Guid, int>
            {
                [volId] = 2,
                [ptId] = 2,
                [chId] = 2,
            };
            var ctx = Create(counts);
            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: false, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                NodeCharacterParagraphCounts: counts));
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
            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: false, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                NodeCharacterParagraphCounts: counts));
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
            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: false, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [],
                NodeCharacterParagraphCounts: counts));
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
            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: true, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [nodeId], NodeCharacterParagraphCounts: new Dictionary<Guid, int>()));

            await ctx.Presenter.LoadAsync(Folder);

            Assert.True(ctx.Presenter.IsNodeSelectable(nodeId));
        }

        [Fact]
        public async Task IsNodeSelectable_NodeWithoutCharacterParagraphs_False()
        {
            var ctx = Create();
            ctx.Reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: true, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [], NodeCharacterParagraphCounts: new Dictionary<Guid, int>()));

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
            var presenter = new BookHierarchyPresenter(reader, commandHandler, new FakeBookUseCases(),
                treeState, selectionState, audioSelectionState, dialogService, snackbar, paragraphTtsSettings, queue, new AudioQueueService());
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
                Arg.Is<SetParagraphCharacterCommand>(c => c.ParagraphId == paragraph.Id && c.CharacterId == null));
            await ctx.CommandHandler.DidNotReceive().ExecuteAsync(Arg.Any<SetItemCharacterCommand>());
        }

        // ---------------------------------------------------------------
        // OnQueueChanged — presenter stamps resolved character onto tree
        // ---------------------------------------------------------------

        private static async Task<(Context ctx, CharacterQueueService queue, Paragraph para, QueuedParagraph queued)>
            CreateWithLoadedParagraph()
        {
            var chapterId = Guid.NewGuid();
            var para = new Paragraph
            {
                Id = Guid.NewGuid(),
                Items =
                [
                    new ParagraphItem { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Order = "a" },
                ]
            };

            var reader = Substitute.For<IProjectReader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            reader.GetBookOverviewAsync(Folder).Returns(new BookOverview(
                Filename: null, HasContent: true, Volumes: [], Characters: [],
                TotalParts: 0, TotalChapters: 0, SelectableNodeIds: [], NodeCharacterParagraphCounts: new Dictionary<Guid, int>()));
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
            var presenter = new BookHierarchyPresenter(reader, commandHandler, bookUseCases, treeState, selectionState, audioSelectionState, dialogService, snackbar2, paragraphTtsSettings2, queue, new AudioQueueService());

            await presenter.LoadAsync(Folder);
            // Expand chapter so paragraphs are loaded into the cache.
            await presenter.Tree.OnChapterExpandedAsync(new Chapter { Id = chapterId, Order = "a" }, expanded: true);

            var ctx = new Context(presenter, reader, commandHandler, bookUseCases, treeState);
            var queuedPara = new QueuedParagraph(Folder, para.Id, "preview", chapterId, Guid.NewGuid(), Guid.NewGuid());
            return (ctx, queue, para, queuedPara);
        }

        [Fact]
        public async Task OnQueueChanged_DoesNotMutateParagraphItems_WhenResolved()
        {
            // Items stay unstamped; ParagraphRow reads Queue.ResolvedOf at render time instead.
            var (ctx, queue, para, queued) = await CreateWithLoadedParagraph();

            var charId = Guid.NewGuid();
            var resolved = new ResolvedCharacter(charId, "Alice");

            queue.Enqueue([queued]);
            queue.MarkProcessing(queued);
            queue.MarkComplete(queued, elapsedSeconds: 1.0, resolved);

            var item = para.Items.First();
            Assert.Null(item.CharacterId);
            Assert.Null(item.Character);
        }

        [Fact]
        public async Task OnQueueChanged_ResolvedOf_ReturnsResolvedCharacter()
        {
            var (ctx, queue, para, queued) = await CreateWithLoadedParagraph();

            var charId = Guid.NewGuid();
            var resolved = new ResolvedCharacter(charId, "Alice");

            queue.Enqueue([queued]);
            queue.MarkProcessing(queued);
            queue.MarkComplete(queued, elapsedSeconds: 1.0, resolved);

            var result = queue.ResolvedOf(Folder, para.Id);
            Assert.NotNull(result);
            Assert.Equal(charId, result!.CharacterId);
            Assert.Equal("Alice", result.Name);
        }

        [Fact]
        public async Task OnQueueChanged_ParagraphNotInTree_DoesNotThrow()
        {
            var (ctx, queue, _, _) = await CreateWithLoadedParagraph();

            // Resolve a paragraph not present in the tree.
            var foreignPara = new QueuedParagraph(Folder, Guid.NewGuid(), "x", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            queue.Enqueue([foreignPara]);
            queue.MarkProcessing(foreignPara);
            var ex = Record.Exception(() => queue.MarkComplete(foreignPara, 1.0, new ResolvedCharacter(Guid.NewGuid(), "Bob")));

            Assert.Null(ex);
        }
    }
}
