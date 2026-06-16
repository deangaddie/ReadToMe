using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.UseCases;
using Xunit;

namespace Read2Me.Tests.State
{
    // Fake BookUseCases: controllable import results without real dependencies.
    internal class FakeBookUseCases : BookUseCases
    {
        private Result _result = Result.Ok();

        public FakeBookUseCases() : base(null!, null!, null!) { }

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

        private static Context Create()
        {
            var reader = Substitute.For<IProjectReader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            // Default reader returns
            reader.GetProjectAsync(Folder).Returns((Project?)null);
            reader.HasBookContentAsync(Folder).Returns(false);
            reader.GetVolumesAsync(Folder).Returns(new List<Volume>());
            reader.GetCharactersAsync(Folder).Returns(new List<Character>());
            reader.GetTotalPartCountAsync(Folder).Returns(0);
            reader.GetTotalChapterCountAsync(Folder).Returns(0);

            // Selection-related reader defaults
            reader.GetVolumeCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetPartCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetChapterCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetVolumeUnprocessedCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetPartUnprocessedCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetChapterUnprocessedCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetVolumeCharacterParagraphCountAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(0);
            reader.GetPartCharacterParagraphCountAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(0);
            reader.GetChapterCharacterParagraphCountAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(0);
            reader.GetNodesWithCharacterParagraphsAsync(Arg.Any<ProjectFolderId>())
                .Returns(new HashSet<Guid>());

            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var selectionCoordinator = new SelectionCoordinator(reader);
            var characterQueue = new CharacterQueueService();
            var presenter = new BookHierarchyPresenter(reader, commandHandler, bookUseCases, treeState, selectionState, selectionCoordinator, dialogService, characterQueue);
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
            ctx.Reader.HasBookContentAsync(Folder).Returns(true);
            ctx.Reader.GetVolumesAsync(Folder).Returns(new List<Volume> { vol });
            ctx.Reader.GetCharactersAsync(Folder).Returns(new List<Character>());
            ctx.Reader.GetTotalPartCountAsync(Folder).Returns(1);
            ctx.Reader.GetTotalChapterCountAsync(Folder).Returns(1);
            ctx.Reader.GetPartsAsync(Folder, vol.Id).Returns(new List<Part>());

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

            ctx.Reader.HasBookContentAsync(Folder).Returns(true);
            ctx.Reader.GetVolumesAsync(Folder).Returns(new List<Volume>());
            ctx.Reader.GetCharactersAsync(Folder).Returns(new List<Character> { character });
            ctx.Reader.GetTotalPartCountAsync(Folder).Returns(0);
            ctx.Reader.GetTotalChapterCountAsync(Folder).Returns(0);
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
            // sourcePartId NOT in ExpandedPartIds
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
            // Load with empty character list
            await ctx.Presenter.LoadAsync(Folder);
            Assert.Empty(ctx.Presenter.Characters);

            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "NewChar" };

            // Reader returns the character on refresh
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

            // Manually add a paragraph to selection.
            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            ctx.Presenter.Selection.AddParagraph(Guid.NewGuid(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);

            // Load the same folder again (tab switch simulation).
            await ctx.Presenter.LoadAsync(Folder);

            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task LoadAsync_DifferentFolder_ClearsPreviousFolderSelection()
        {
            var ctx = Create();
            var other = new ProjectFolderId("other-book");

            // Also stub the new folder
            ctx.Reader.GetProjectAsync(other).Returns((Project?)null);
            ctx.Reader.HasBookContentAsync(other).Returns(false);
            ctx.Reader.GetVolumesAsync(other).Returns(new List<Volume>());
            ctx.Reader.GetCharactersAsync(other).Returns(new List<Character>());
            ctx.Reader.GetTotalPartCountAsync(other).Returns(0);
            ctx.Reader.GetTotalChapterCountAsync(other).Returns(0);

            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            ctx.Presenter.Selection.AddParagraph(Guid.NewGuid(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(1, ctx.Presenter.Selection.SelectedParagraphCount);

            // Switch to different folder — previous folder selection clears.
            await ctx.Presenter.LoadAsync(other);

            // Previous folder's selection is cleared (Reset was called).
            // We can verify by switching back and checking.
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
        public async Task ToggleParagraphAsync_CompleteChapter_AddsChapterNode()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var pId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();

            // Chapter has exactly 1 character paragraph — selecting it completes the chapter.
            ctx.Reader.GetChapterCharacterParagraphCountAsync(Folder, chId).Returns(1);

            await ctx.Presenter.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(chId));
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

            ctx.Reader.GetChapterCharacterParagraphsAsync(Folder, chId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId1, chId, ptId, volId),
                    new CharacterParagraphRef(pId2, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Chapter, chId, on: true);

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

            ctx.Reader.GetChapterCharacterParagraphsAsync(Folder, chId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Chapter, chId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Chapter, chId, on: false);

            Assert.False(ctx.Presenter.Selection.IsParagraphSelected(pId));
        }

        [Fact]
        public async Task SetNodeAsync_Volume_On_MarksDescendantChaptersAndPartsChecked()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId1 = Guid.NewGuid(); var pId2 = Guid.NewGuid();

            ctx.Reader.GetVolumeCharacterParagraphsAsync(Folder, volId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId1, chId, ptId, volId),
                    new CharacterParagraphRef(pId2, chId, ptId, volId),
                });
            // Volume completeness so the volume node itself is marked.
            ctx.Reader.GetVolumeCharacterParagraphCountAsync(Folder, volId).Returns(2);

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Volume, volId, on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(volId));
            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(ptId));
            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(chId));
        }

        [Fact]
        public async Task SetNodeAsync_Part_On_MarksDescendantChaptersChecked()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            ctx.Reader.GetPartCharacterParagraphsAsync(Folder, ptId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(chId));
        }

        [Fact]
        public async Task SetNodeAsync_Part_Off_ClearsDescendantChapterNodes()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            ctx.Reader.GetPartCharacterParagraphsAsync(Folder, ptId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: false);

            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(chId));
            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(ptId));
        }

        [Fact]
        public async Task SetNodeAsync_Volume_Off_ClearsDescendantPartAndChapterNodes()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId1 = Guid.NewGuid(); var pId2 = Guid.NewGuid();

            ctx.Reader.GetVolumeCharacterParagraphsAsync(Folder, volId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId1, chId, ptId, volId),
                    new CharacterParagraphRef(pId2, chId, ptId, volId),
                });
            ctx.Reader.GetVolumeCharacterParagraphCountAsync(Folder, volId).Returns(2);

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Volume, volId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Volume, volId, on: false);

            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(volId));
            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(ptId));
            Assert.Equal(TriState.Unchecked, ctx.Presenter.Selection.NodeState(chId));
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

            ctx.Reader.GetPartCharacterParagraphsAsync(Folder, ptId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: false);

            Assert.False(ctx.Presenter.Selection.IsParagraphSelected(pId));
            Assert.Equal(0, ctx.Presenter.Selection.SelectedParagraphCount);
        }

        [Fact]
        public async Task SetNodeAsync_Part_OnOffOn_ReselectsCleanly()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var volId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var chId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            ctx.Reader.GetPartCharacterParagraphsAsync(Folder, ptId)
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId, chId, ptId, volId),
                });

            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: true);
            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: false);
            await ctx.Presenter.SetNodeAsync(Folder, SelectionNodeKind.Part, ptId, on: true);

            Assert.Equal(TriState.Checked, ctx.Presenter.Selection.NodeState(chId));
            Assert.True(ctx.Presenter.Selection.IsParagraphSelected(pId));
        }

        // ---------------------------------------------------------------
        // Selectable nodes (checkbox only shown for nodes with char paragraphs)
        // ---------------------------------------------------------------

        [Fact]
        public async Task IsNodeSelectable_NodeWithCharacterParagraphs_True()
        {
            var ctx = Create();
            var nodeId = Guid.NewGuid();
            ctx.Reader.HasBookContentAsync(Folder).Returns(true);
            ctx.Reader.GetVolumesAsync(Folder).Returns(new List<Volume>());
            ctx.Reader.GetNodesWithCharacterParagraphsAsync(Folder)
                .Returns(new HashSet<Guid> { nodeId });

            await ctx.Presenter.LoadAsync(Folder);

            Assert.True(ctx.Presenter.IsNodeSelectable(nodeId));
        }

        [Fact]
        public async Task IsNodeSelectable_NodeWithoutCharacterParagraphs_False()
        {
            var ctx = Create();
            ctx.Reader.HasBookContentAsync(Folder).Returns(true);
            ctx.Reader.GetVolumesAsync(Folder).Returns(new List<Volume>());
            ctx.Reader.GetNodesWithCharacterParagraphsAsync(Folder)
                .Returns(new HashSet<Guid>());

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

            // Need access to the CharacterQueueService used by the presenter.
            // Re-create the context keeping the same queue instance so we can inspect it.
            var reader = ctx.Reader;
            var commandHandler = ctx.CommandHandler;
            var dialogService = Substitute.For<IDialogService>();
            var queue = new CharacterQueueService();
            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var selectionState = new BookSelectionState();
            var selectionCoordinator = new SelectionCoordinator(reader);
            var presenter = new BookHierarchyPresenter(reader, commandHandler, new FakeBookUseCases(),
                treeState, selectionState, selectionCoordinator, dialogService, queue);
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
    }
}
